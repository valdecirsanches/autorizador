import { Analytics } from '@vercel/analytics/next'
import type { Metadata, Viewport } from 'next'
import { Geist, Geist_Mono } from 'next/font/google'
import { Toaster } from '@/components/ui/sonner'
import './globals.css'

const geistSans = Geist({
  subsets: ['latin'],
  variable: '--font-sans',
})

const geistMono = Geist_Mono({
  subsets: ['latin'],
  variable: '--font-mono',
})

export const metadata: Metadata = {
  title: 'Autorizador NF-e | Painel',
  description:
    'Painel de operação do autorizador de NF-e: status dos serviços da SEFAZ por UF, empresas emitentes e certificados digitais A1.',
  generator: 'v0.app',
}

export const viewport: Viewport = {
  colorScheme: 'light dark',
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: 'oklch(0.985 0.003 250)' },
    { media: '(prefers-color-scheme: dark)', color: 'oklch(0.17 0.02 256)' },
  ],
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode
}>) {
  return (
    <html lang="pt-BR" className={`bg-background ${geistSans.variable} ${geistMono.variable}`}>
      <body className="font-sans antialiased">
        {children}
        <Toaster richColors position="top-right" />
        {/* O script do Vercel Analytics so existe quando hospedado na Vercel. Fora
            dela (ex.: EC2), /_vercel/insights cai no middleware, volta HTML e gera
            "Unexpected token '<'" no console de toda pagina. */}
        {process.env.NODE_ENV === 'production' && process.env.VERCEL === '1' && <Analytics />}
      </body>
    </html>
  )
}
