import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

const compact = new Intl.NumberFormat('ru-RU', { notation: 'compact', maximumFractionDigits: 1 })

export default function TrendChart({ points }: { points: { name: string; value: number; exact: string }[] }) {
  return (
    <div className="chart-body" role="img" aria-label={points.map(point => `${point.name}: ${point.exact}`).join('; ')}>
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={points} margin={{ top: 12, right: 12, left: 0, bottom: 4 }}>
          <defs><linearGradient id="royalty-area" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="#6e59e9" stopOpacity={0.22} /><stop offset="100%" stopColor="#6e59e9" stopOpacity={0.015} />
          </linearGradient></defs>
          <CartesianGrid vertical={false} strokeDasharray="4 4" stroke="#e5e8f0" />
          <XAxis dataKey="name" tick={{ fill: '#58657a', fontSize: 12 }} axisLine={false} tickLine={false} minTickGap={24} />
          <YAxis tick={{ fill: '#58657a', fontSize: 12 }} axisLine={false} tickLine={false} width={66}
            tickFormatter={value => compact.format(value)} />
          <Tooltip formatter={(_, __, entry) => [entry.payload.exact, 'Роялти']} />
          <Area type="monotone" dataKey="value" stroke="#6e59e9" strokeWidth={3} fill="url(#royalty-area)"
            dot activeDot={{ r: 5 }} isAnimationActive={false} />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  )
}
