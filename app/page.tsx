import { EmpresaProvider } from "@/components/empresa-provider"
import { PainelShell } from "@/components/painel-shell"

export default function Page() {
  return (
    <EmpresaProvider>
      <PainelShell />
    </EmpresaProvider>
  )
}
