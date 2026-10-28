import { execSync } from "node:child_process"
import { readFileSync } from "node:fs"

function git(args) {
  try {
    return execSync(`git ${args}`, { stdio: ["ignore", "pipe", "ignore"] }).toString().trim()
  } catch {
    return ""
  }
}

/**
 * Versao automatica: MAIOR.MENOR vem do package.json (mude la so em mudancas
 * grandes) e o PATCH e o numero de commits. Cada atualizacao publicada gera
 * um commit, entao a versao sobe sozinha. Em clone raso (contagem incorreta)
 * ou sem git, usa a versao do package.json.
 */
function resolverVersao() {
  const pkg = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8"))
  const [maior = "0", menor = "0"] = String(pkg.version).split(".")

  const raso = git("rev-parse --is-shallow-repository") === "true"
  const contagem = raso ? "" : git("rev-list --count HEAD")
  const hash = git("rev-parse HEAD").slice(0, 7)

  return {
    versao: contagem ? `${maior}.${menor}.${contagem}` : String(pkg.version),
    hash,
  }
}

const { versao, hash } = resolverVersao()

/** @type {import('next').NextConfig} */
const nextConfig = {
  typescript: {
    ignoreBuildErrors: true,
  },
  images: {
    unoptimized: true,
  },
  env: {
    NEXT_PUBLIC_APP_VERSION: versao,
    NEXT_PUBLIC_APP_COMMIT: hash,
    NEXT_PUBLIC_APP_BUILD_DATE: new Date().toISOString(),
    // Data do build ja formatada no horario de Brasilia, para exibir igual no
    // servidor e no navegador (sem diferenca de fuso na hidratacao).
    NEXT_PUBLIC_APP_BUILD_LABEL: new Intl.DateTimeFormat("pt-BR", {
      dateStyle: "short",
      timeStyle: "short",
      timeZone: "America/Sao_Paulo",
    }).format(new Date()),
  },
}

export default nextConfig
