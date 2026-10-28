import { cn } from "@/lib/utils"

type Tom = "success" | "warning" | "destructive" | "muted"

const tons: Record<Tom, string> = {
  success: "bg-success/15 text-success border-success/30",
  warning: "bg-warning/15 text-warning-foreground border-warning/40",
  destructive: "bg-destructive/15 text-destructive border-destructive/30",
  muted: "bg-muted text-muted-foreground border-border",
}

export function StatusBadge({
  tom,
  children,
  className,
}: {
  tom: Tom
  children: React.ReactNode
  className?: string
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium",
        tons[tom],
        className,
      )}
    >
      <span className={cn("size-1.5 rounded-full", `bg-current`)} aria-hidden />
      {children}
    </span>
  )
}
