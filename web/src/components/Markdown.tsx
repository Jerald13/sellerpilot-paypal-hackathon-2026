import type { ReactNode } from 'react'

// Minimal markdown for assistant replies: paragraphs, bullet/numbered lists, **bold** and `code`.
// Built from React elements only (no innerHTML), so model output can never inject markup.

function inline(text: string): ReactNode[] {
  const parts: ReactNode[] = []
  const pattern = /(\*\*[^*]+\*\*|`[^`]+`)/g
  let last = 0
  let key = 0
  for (const match of text.matchAll(pattern)) {
    const index = match.index ?? 0
    if (index > last) parts.push(text.slice(last, index))
    const token = match[0]
    parts.push(
      token.startsWith('**') ? <strong key={key++}>{token.slice(2, -2)}</strong> : <code key={key++}>{token.slice(1, -1)}</code>,
    )
    last = index + token.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

export function Markdown({ text }: { text: string }) {
  const blocks: ReactNode[] = []
  let list: { ordered: boolean; items: string[] } | null = null
  let paragraph: string[] = []

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push(<p key={blocks.length}>{inline(paragraph.join(' '))}</p>)
      paragraph = []
    }
  }
  const flushList = () => {
    if (list) {
      const items = list.items.map((item, i) => <li key={i}>{inline(item)}</li>)
      blocks.push(list.ordered ? <ol key={blocks.length}>{items}</ol> : <ul key={blocks.length}>{items}</ul>)
      list = null
    }
  }

  for (const raw of text.split('\n')) {
    const line = raw.trim()
    const bullet = /^[-*•]\s+(.*)$/.exec(line)
    const numbered = /^\d+[.)]\s+(.*)$/.exec(line)
    const heading = /^#{1,6}\s+(.*)$/.exec(line)
    if (bullet || numbered) {
      flushParagraph()
      const ordered = Boolean(numbered)
      if (!list || list.ordered !== ordered) {
        flushList()
        list = { ordered, items: [] }
      }
      list.items.push((bullet ?? numbered)![1])
    } else if (heading) {
      flushParagraph()
      flushList()
      blocks.push(<p key={blocks.length}><strong>{inline(heading[1])}</strong></p>)
    } else if (line === '') {
      flushParagraph()
      flushList()
    } else {
      flushList()
      paragraph.push(line)
    }
  }
  flushParagraph()
  flushList()

  return <div className="markdown">{blocks}</div>
}
