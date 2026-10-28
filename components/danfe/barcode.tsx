"use client"

import { useEffect, useRef } from "react"
import JsBarcode from "jsbarcode"

// Codigo de barras da chave de acesso no padrao do DANFE: CODE128C, sem texto
// legivel embaixo (a chave aparece formatada em campo proprio). Renderiza em SVG
// para ficar nitido na impressao.
export function Barcode({ value, height = 50 }: { value: string; height?: number }) {
  const ref = useRef<SVGSVGElement>(null)

  useEffect(() => {
    if (!ref.current || !value) return
    try {
      JsBarcode(ref.current, value, {
        format: "CODE128C",
        displayValue: false,
        height,
        width: 1.4,
        margin: 0,
        background: "#ffffff",
        lineColor: "#000000",
      })
    } catch {
      // chave invalida: deixa vazio, o campo de texto ainda mostra a chave
    }
  }, [value, height])

  return <svg ref={ref} className="w-full" role="img" aria-label={`Código de barras: ${value}`} />
}
