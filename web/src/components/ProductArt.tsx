import type { ReactElement } from 'react'

// Original illustrations of the demo products (drawn for this project, no stock images), one per SKU.
// viewBox 0 0 160 120; each sits on a soft tile so the shop reads well in both themes.

const art: Record<string, { label: string; bg: string; svg: ReactElement }> = {
  'SCARF-BLUE': {
    label: 'Blue silk scarf',
    bg: '#e3ecfb',
    svg: (
      <g>
        <path d="M86 52 100 56 74 108 60 103Z" fill="#2f6fdc" stroke="#1d4aa0" strokeWidth="2" />
        <ellipse cx="80" cy="36" rx="26" ry="20" fill="none" stroke="#1d4aa0" strokeWidth="17" />
        <ellipse cx="80" cy="36" rx="26" ry="20" fill="none" stroke="#3d82ef" strokeWidth="13" />
        <path d="M64 50 78 47 106 101 92 106Z" fill="#3d82ef" stroke="#1d4aa0" strokeWidth="2" />
        <path d="M68 60 82 57M73 70 87 67M78 80 92 77M83 90 97 87" stroke="#a9c6f7" strokeWidth="2" />
        <g stroke="#2f6fdc" strokeWidth="2.4" strokeLinecap="round">
          <path d="M62 104 60 114M67 106 66 116M72 108 72 117" />
          <path d="M93 107 92 117M98 105 99 115M103 103 105 112" />
        </g>
      </g>
    ),
  },
  'RUNNER-SET': {
    label: 'Linen table runner set',
    bg: '#f3ece1',
    svg: (
      <g>
        <path d="M14 70 146 70 132 104 28 104Z" fill="#b98a5e" />
        <path d="M28 104 132 104 132 110 28 110Z" fill="#8f6643" />
        <path d="M60 70 100 70 104 104 56 104Z" fill="#efe3cf" stroke="#c9b28f" strokeWidth="1.5" />
        <path d="M61 78 99 78M60 86 101 86M59 94 102 94" stroke="#c0553b" strokeWidth="2.5" />
        <g stroke="#c9b28f" strokeWidth="1.6">
          <path d="M58 104 57 110M64 104 64 110M70 104 70 110M76 104 76 110M82 104 82 110M88 104 88 110M94 104 94 110M100 104 101 110" />
        </g>
        <rect x="44" y="18" width="72" height="34" rx="3" fill="#efe3cf" stroke="#c9b28f" strokeWidth="1.5" />
        <path d="M44 28 116 28M44 42 116 42" stroke="#c0553b" strokeWidth="2.5" />
        <text x="80" y="38" textAnchor="middle" fontSize="9" fontWeight="700" fill="#8f6643">×2</text>
      </g>
    ),
  },
  WRAP: {
    label: 'Hand-stamped cotton wrap',
    bg: '#fbe9e2',
    svg: (
      <g>
        <path d="M30 26 130 26 126 96 34 96Z" fill="#e0734f" stroke="#a9462a" strokeWidth="2" />
        <path d="M34 96 126 96 120 106 40 106Z" fill="#c95b39" />
        <path d="M54 28 50 94M106 28 110 94" stroke="#c95b39" strokeWidth="2" />
        {[40, 62, 84].map((y) =>
          [62, 80, 98].map((x) => <path key={`${x}-${y}`} d={`M${x} ${y - 7} ${x + 7} ${y} ${x} ${y + 7} ${x - 7} ${y}Z`} fill="#fde2c4" opacity="0.9" />),
        )}
      </g>
    ),
  },
  'SHIRT-L': {
    label: 'Cotton shirt (final sale)',
    bg: '#e5f3ec',
    svg: (
      <g>
        <path d="M56 18 68 14Q80 24 92 14L104 18 128 36 116 52 106 44 106 106 54 106 54 44 44 52 32 36Z" fill="#2e9d6a" stroke="#1f6e4a" strokeWidth="2" />
        <path d="M68 14Q80 30 92 14" fill="none" stroke="#1f6e4a" strokeWidth="2" />
        <path d="M80 26 80 106" stroke="#1f6e4a" strokeWidth="1.5" />
        {[38, 54, 70, 86].map((y) => (
          <circle key={y} cx="80" cy={y} r="2" fill="#e5f3ec" />
        ))}
        {[[64, 60], [96, 72], [66, 86], [94, 94], [62, 100]].map(([x, y]) => (
          <circle key={`${x}-${y}`} cx={x} cy={y} r="3.2" fill="#9fdcbf" />
        ))}
        <g transform="rotate(-12 118 90)">
          <rect x="92" y="81" width="52" height="17" rx="3" fill="#c0392b" />
          <text x="118" y="92.5" textAnchor="middle" fontSize="7" fontWeight="800" fill="#fff">FINAL SALE</text>
        </g>
      </g>
    ),
  },
  COASTERS: {
    label: 'Coaster set',
    bg: '#f6efe0',
    svg: (
      <g>
        {[0, 1, 2, 3].map((i) => (
          <g key={i}>
            <ellipse cx="80" cy={92 - i * 12} rx="40" ry="14" fill={i % 2 ? '#d9a441' : '#2f6fdc'} stroke="#5c4a2a" strokeWidth="1.5" />
          </g>
        ))}
        <ellipse cx="80" cy="56" rx="40" ry="14" fill="#2f6fdc" stroke="#5c4a2a" strokeWidth="1.5" />
        <ellipse cx="80" cy="56" rx="26" ry="8" fill="none" stroke="#f6efe0" strokeWidth="2" strokeDasharray="4 3" />
        <ellipse cx="80" cy="56" rx="10" ry="3.5" fill="#f6efe0" />
      </g>
    ),
  },
  CUSHIONS: {
    label: 'Cushion covers (pair)',
    bg: '#efe6f6',
    svg: (
      <g>
        <rect x="18" y="34" width="64" height="62" rx="12" fill="#7d5bbe" stroke="#563b8f" strokeWidth="2" transform="rotate(-6 50 65)" />
        <rect x="70" y="30" width="66" height="66" rx="12" fill="#e6b84a" stroke="#a98126" strokeWidth="2" transform="rotate(5 103 63)" />
        <g transform="rotate(5 103 63)" stroke="#fff6dc" strokeWidth="2.5" fill="none">
          <path d="M86 50 120 50M86 63 120 63M86 76 120 76" />
        </g>
        <g transform="rotate(-6 50 65)" fill="#c9b6ea">
          <circle cx="38" cy="54" r="4" />
          <circle cx="62" cy="54" r="4" />
          <circle cx="50" cy="66" r="4" />
          <circle cx="38" cy="78" r="4" />
          <circle cx="62" cy="78" r="4" />
        </g>
      </g>
    ),
  },
  TOTE: {
    label: 'Canvas tote bag',
    bg: '#ece8df',
    svg: (
      <g>
        <path d="M60 46Q60 14 80 14Q100 14 100 46" fill="none" stroke="#6b5a3e" strokeWidth="5" />
        <path d="M44 44 116 44 110 108 50 108Z" fill="#d8c8a6" stroke="#8c7754" strokeWidth="2" />
        <path d="M44 44 116 44 115 54 45 54Z" fill="#c4b18b" />
        <circle cx="80" cy="80" r="16" fill="none" stroke="#c0553b" strokeWidth="3" />
        <path d="M70 80 80 70 90 80 80 90Z" fill="#c0553b" />
      </g>
    ),
  },
  'WALL-L': {
    label: 'Wall hanging, large',
    bg: '#e2f1f3',
    svg: (
      <g>
        <path d="M80 6 46 22M80 6 114 22" stroke="#6b5a3e" strokeWidth="1.8" />
        <circle cx="80" cy="6" r="2.5" fill="#6b5a3e" />
        <rect x="36" y="20" width="88" height="5" rx="2.5" fill="#8f6643" />
        <rect x="42" y="25" width="76" height="72" fill="#1f8a95" stroke="#145e66" strokeWidth="2" />
        <path d="M42 49 80 33 118 49M42 73 80 57 118 73M42 97 80 81 118 97" fill="none" stroke="#f2c46d" strokeWidth="3" />
        <g stroke="#145e66" strokeWidth="2" strokeLinecap="round">
          <path d="M48 97 48 110M58 97 58 112M68 97 68 110M80 97 80 113M92 97 92 110M102 97 102 112M112 97 112 110" />
        </g>
      </g>
    ),
  },
  KEYCHAIN: {
    label: 'Fabric keychain',
    bg: '#fdeee6',
    svg: (
      <g>
        <circle cx="80" cy="30" r="16" fill="none" stroke="#9aa3ad" strokeWidth="4" />
        <path d="M80 46 80 54" stroke="#9aa3ad" strokeWidth="3" />
        <rect x="68" y="54" width="24" height="10" rx="3" fill="#9aa3ad" />
        <path d="M64 64 96 64 100 104 80 96 60 104Z" fill="#e0734f" stroke="#a9462a" strokeWidth="2" />
        <path d="M70 74 90 74M68 84 92 84" stroke="#fde2c4" strokeWidth="2.5" />
      </g>
    ),
  },
  BOOKMARKS: {
    label: 'Bookmark set',
    bg: '#eef0e2',
    svg: (
      <g>
        {[
          { x: 46, r: -10, c: '#2f6fdc' },
          { x: 72, r: 0, c: '#c0553b' },
          { x: 98, r: 10, c: '#2e9d6a' },
        ].map((b) => (
          <g key={b.x} transform={`rotate(${b.r} ${b.x + 8} 60)`}>
            <path d={`M${b.x} 18 ${b.x + 16} 18 ${b.x + 16} 94 ${b.x + 8} 86 ${b.x} 94Z`} fill={b.c} stroke="#3b3b3b" strokeWidth="1" />
            <path d={`M${b.x + 4} 30 ${b.x + 12} 30M${b.x + 4} 44 ${b.x + 12} 44M${b.x + 4} 58 ${b.x + 12} 58`} stroke="#f7f4ea" strokeWidth="2" />
            <path d={`M${b.x + 8} 18 ${b.x + 8} 8`} stroke="#3b3b3b" strokeWidth="1.2" />
            <circle cx={b.x + 8} cy="7" r="2.5" fill={b.c} />
          </g>
        ))}
      </g>
    ),
  },
}

const fallback = (
  <g>
    <rect x="40" y="26" width="80" height="68" rx="8" fill="#c9b28f" />
    <path d="M40 46 120 46M40 66 120 66" stroke="#efe3cf" strokeWidth="3" />
  </g>
)

export function ProductArt({ sku, className }: { sku: string; className?: string }) {
  const item = art[sku]
  return (
    <svg className={`product-art ${className ?? ''}`} viewBox="0 0 160 120" role="img" aria-label={item?.label ?? 'Product'} style={{ background: item?.bg ?? '#efe9df' }}>
      {item?.svg ?? fallback}
    </svg>
  )
}
