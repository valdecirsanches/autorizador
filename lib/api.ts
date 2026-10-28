/**
 * Cliente do painel. Fala com o proxy same-origin (/api/proxy/*), que por sua
 * vez encaminha para a API .NET. Assim o navegador nunca precisa alcancar o
 * host interno da API diretamente.
 */

const PROXY = '/api/proxy/'

/** Junta o prefixo do proxy com o path, sem barra dupla ou faltante. */
function url(path: string) {
  return PROXY + path.replace(/^\//, '')
}

export class ApiError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function parse<T = unknown>(resp: Response): Promise<T> {
  const texto = await resp.text()

  // A API deve responder JSON, mas erros de infra (ex.: exceção de banco na
  // página de erro do ASP.NET) podem vir como texto/HTML. Nesse caso não
  // quebramos com "Unexpected token": usamos o texto cru como mensagem.
  let dado: unknown = null
  if (texto) {
    try {
      dado = JSON.parse(texto)
    } catch {
      dado = null
    }
  }

  if (!resp.ok) {
    // Sessao do PAINEL expirada/ausente: leva o usuario de volta ao login,
    // preservando o destino atual para retornar apos autenticar. So tratamos
    // como fim de sessao quando o 401 vem marcado pelo middleware/proxy com
    // x-sessao-expirada. Um 401 do backend .NET (ex.: token da API, "Nao
    // autorizado.") NAO expulsa o usuario — vira erro normal, exibido na tela.
    const sessaoExpirada = resp.status === 401 && resp.headers.get('x-sessao-expirada') === '1'
    if (sessaoExpirada && typeof window !== 'undefined') {
      const atual = window.location.pathname + window.location.search
      if (window.location.pathname !== '/login') {
        window.location.href = `/login?de=${encodeURIComponent(atual)}`
      }
    }

    const doJson =
      dado && typeof dado === 'object' && 'erro' in dado
        ? (dado as { erro?: unknown }).erro
        : null
    const detalheJson =
      dado && typeof dado === 'object' && 'detalhe' in dado
        ? (dado as { detalhe?: unknown }).detalhe
        : null
    // Muitos 422 (ex.: validacao da NFS-e) trazem o detalhe acionavel em `erros`
    // (lista), enquanto `erro` e so um resumo generico ("Dados invalidos"). Sem
    // anexar a lista, a causa real — e como corrigir — some da tela.
    const listaErros =
      dado && typeof dado === 'object' && Array.isArray((dado as { erros?: unknown }).erros)
        ? ((dado as { erros?: unknown[] }).erros as unknown[])
            .map((e) => String(e))
            .filter((e) => e.length > 0)
        : []
    // Texto cru truncado como último recurso, para a causa não sumir.
    const bruto = !dado && texto ? texto.slice(0, 300) : null
    const resumo = doJson || bruto || `Erro ${resp.status}`
    // Preserva o detalhe tecnico enviado pela API (ex.: coluna de banco ausente)
    // para que telas de diagnostico nao reduzam todo erro a uma mensagem generica.
    const detalheServidor = typeof detalheJson === 'string' ? detalheJson.trim().slice(0, 500) : ''
    const detalhe = listaErros.filter((e) => e !== String(resumo)).join(' ')
    const partes = [String(resumo), detalheServidor, detalhe].filter(Boolean)
    throw new ApiError([...new Set(partes)].join(' '), resp.status)
  }

  return dado as T
}

/**
 * Fetcher para o SWR. O tipo concreto vem do parametro generico de cada
 * `useSWR<T>(...)`; aqui devolvemos o `T` inferido apos o parse em runtime.
 */
export const fetcher = <T = unknown>(path: string): Promise<T> =>
  fetch(url(path), { cache: 'no-store' }).then((r) => parse<T>(r))

export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const resp = await fetch(url(path), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })
  return parse<T>(resp)
}

export async function putJson<T>(path: string, body: unknown): Promise<T> {
  const resp = await fetch(url(path), {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })
  return parse<T>(resp)
}

export async function postForm<T>(path: string, form: FormData): Promise<T> {
  // Sem content-type manual: o browser define o boundary do multipart.
  const resp = await fetch(url(path), { method: 'POST', body: form })
  return parse<T>(resp)
}

export async function del<T>(path: string): Promise<T> {
  const resp = await fetch(url(path), { method: 'DELETE' })
  return parse<T>(resp)
}
