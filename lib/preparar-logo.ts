/**
 * Normaliza o logo no navegador antes do upload.
 *
 * Por que: proxies reversos (nginx usa `client_max_body_size 1m` por padrao)
 * recusam corpos grandes antes de chegarem a API, e formatos como SVG/AVIF nao
 * sao aceitos pelo DANFE. Redimensionando e reencodando em PNG (ou JPEG, se o
 * PNG ficar grande), o arquivo enviado fica pequeno e sempre num formato aceito.
 */

const LADO_MAXIMO = 600
const BYTES_ALVO = 700 * 1024
const BYTES_ORIGINAL_ACEITO = 300 * 1024
const TIPOS_ACEITOS_DIRETO = new Set(["image/png", "image/jpeg"])

export type LogoPreparado = { blob: Blob; nome: string }

async function decodificar(arquivo: File): Promise<HTMLImageElement> {
  const url = URL.createObjectURL(arquivo)
  try {
    const img = new Image()
    img.decoding = "async"
    img.src = url
    await img.decode()
    return img
  } catch {
    throw new Error(
      "O navegador não conseguiu ler esta imagem (ex.: HEIC/TIFF). Converta para PNG ou JPEG e tente novamente.",
    )
  } finally {
    URL.revokeObjectURL(url)
  }
}

function paraBlob(canvas: HTMLCanvasElement, tipo: string, qualidade?: number): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (b) => (b ? resolve(b) : reject(new Error("Falha ao converter a imagem."))),
      tipo,
      qualidade,
    )
  })
}

export async function prepararLogo(arquivo: File): Promise<LogoPreparado> {
  const img = await decodificar(arquivo)
  const largura = img.naturalWidth || img.width
  const altura = img.naturalHeight || img.height
  if (!largura || !altura) throw new Error("Imagem sem dimensões válidas.")

  const cabeSemReduzir = Math.max(largura, altura) <= LADO_MAXIMO
  if (
    cabeSemReduzir &&
    arquivo.size <= BYTES_ORIGINAL_ACEITO &&
    TIPOS_ACEITOS_DIRETO.has(arquivo.type)
  ) {
    return { blob: arquivo, nome: arquivo.name || "logo.png" }
  }

  const escala = Math.min(1, LADO_MAXIMO / Math.max(largura, altura))
  const canvas = document.createElement("canvas")
  canvas.width = Math.max(1, Math.round(largura * escala))
  canvas.height = Math.max(1, Math.round(altura * escala))
  const ctx = canvas.getContext("2d")
  if (!ctx) throw new Error("Seu navegador não suporta conversão de imagens.")
  ctx.imageSmoothingQuality = "high"
  ctx.drawImage(img, 0, 0, canvas.width, canvas.height)

  const png = await paraBlob(canvas, "image/png")
  if (png.size <= BYTES_ALVO) return { blob: png, nome: "logo.png" }

  // JPEG nao tem transparencia: pinta fundo branco (o DANFE e impresso em branco).
  const fundo = document.createElement("canvas")
  fundo.width = canvas.width
  fundo.height = canvas.height
  const fctx = fundo.getContext("2d")!
  fctx.fillStyle = "#ffffff"
  fctx.fillRect(0, 0, fundo.width, fundo.height)
  fctx.drawImage(canvas, 0, 0)
  const jpeg = await paraBlob(fundo, "image/jpeg", 0.85)
  return { blob: jpeg, nome: "logo.jpg" }
}
