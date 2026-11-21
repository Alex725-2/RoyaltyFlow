import { useCallback, useState } from 'react'
import { ChevronDown, Disc3, Search } from 'lucide-react'
import type { Artist, Contract, RightsHolder, RoyaltyApi, Track } from './api'
import { formatDate, integer } from './metrics'
import { DataTable, StateContent, TrackTitle, useResource } from './ui'

type CatalogSection = 'artists' | 'tracks' | 'contracts' | 'holders'
interface CatalogData { artists: Artist[]; tracks: Track[]; contracts: Contract[]; holders: RightsHolder[] }

const catalogHeaders: Record<CatalogSection, string> = {
  artists: 'Каталог исполнителей', tracks: 'Каталог треков', contracts: 'Договоры по трекам', holders: 'Каталог правообладателей',
}

const includes = (value: string, query: string) => value.toLocaleLowerCase('ru').includes(query)

export function Catalog({ section, api }: { section: CatalogSection; api: RoyaltyApi | null }) {
  const [filter, setFilter] = useState('')
  const [artist, setArtist] = useState('')
  const load = useCallback(async (signal: AbortSignal): Promise<CatalogData> => {
    const [artists, tracks, contracts, holders] = await Promise.all([
      section === 'artists' || section === 'tracks' ? api!.artists(signal) : [],
      section === 'holders' ? [] : api!.tracks(signal),
      section === 'contracts' ? api!.contracts(signal) : [],
      section === 'contracts' || section === 'holders' ? api!.holders(signal) : [],
    ])
    return { artists, tracks, contracts, holders }
  }, [api, section])
  const { state, reload } = useResource(api ? load : null)
  const query = filter.trim().toLocaleLowerCase('ru')
  return (
    <section className="panel catalog-panel">
      <div className="panel-heading"><div><h2>{catalogHeaders[section]}</h2>
        <p>{section === 'contracts' ? 'Срок действия и распределение долей' : 'Данные из RoyaltyFlow API'}</p></div>
        <Disc3 size={20} />
      </div>
      <StateContent state={state} retry={reload}>{data => (
        <>
          <div className="catalog-filters">
            <label className="search-field"><Search size={17} aria-hidden="true" />
              <input aria-label="Поиск по каталогу" placeholder="Поиск по названию" value={filter}
                onChange={event => setFilter(event.target.value)} type="search" />
            </label>
            {section === 'tracks' && (
              <label className="select-field">
                <span>Исполнитель</span>
                <span className="select-control"><select value={artist} onChange={event => setArtist(event.target.value)}>
                  <option value="">Все исполнители</option>
                  {[...data.artists].sort((a, b) => a.name.localeCompare(b.name, 'ru')).map(row =>
                    <option key={row.id} value={row.id}>{row.name}</option>)}
                </select><ChevronDown size={16} aria-hidden="true" /></span>
              </label>
            )}
          </div>
          <CatalogTable key={`${query}/${artist}`} data={data} section={section} query={query} artist={artist} />
        </>
      )}</StateContent>
    </section>
  )
}

function CatalogTable({ data, section, query, artist }: {
  data: CatalogData; section: CatalogSection; query: string; artist: string
}) {
  const { artists, tracks, contracts, holders } = data
  const artistNames = new Map(artists.map(row => [row.id, row.name]))
  const trackTitles = new Map(tracks.map(row => [row.id, row.title]))
  const holderNames = new Map(holders.map(row => [row.id, row.name]))
  const labels = (id: string, map: Map<string, string>) => map.get(id) ?? id
  const rowKey = (row: { id: string }) => row.id
  const emptyText = query || artist ? 'По заданным фильтрам ничего не найдено' : 'В каталоге пока нет записей'

  switch (section) {
    case 'artists': {
      const counts = new Map<string, number>()
      for (const track of tracks) counts.set(track.artistId, (counts.get(track.artistId) ?? 0) + 1)
      return <DataTable emptyText={emptyText} rowKey={rowKey}
        rows={artists.filter(row => includes(row.name, query)).sort((a, b) => a.name.localeCompare(b.name, 'ru'))}
        columns={[
          { label: 'Исполнитель', cell: row => <strong>{row.name}</strong> },
          { label: 'Треки', numeric: true, cell: row => integer.format(counts.get(row.id) ?? 0) },
        ]} />
    }
    case 'tracks':
      return <DataTable emptyText={emptyText} rowKey={rowKey}
        rows={tracks.filter(row => (!artist || row.artistId === artist) && includes(row.title, query))
          .sort((a, b) => a.title.localeCompare(b.title, 'ru'))}
        columns={[
          { label: 'Трек', cell: row => <TrackTitle title={row.title} /> },
          { label: 'Исполнитель', cell: row => labels(row.artistId, artistNames) },
        ]} />
    case 'contracts':
      return <DataTable emptyText={emptyText} rowKey={rowKey}
        rows={contracts.filter(row => includes(labels(row.trackId, trackTitles), query))}
        columns={[
          { label: 'Трек', cell: row => <TrackTitle title={labels(row.trackId, trackTitles)} /> },
          { label: 'Действует с', cell: row => formatDate(row.validFrom) },
          { label: 'Действует до', cell: row => row.validTo ? formatDate(row.validTo) : 'Бессрочно' },
          { label: 'Правообладатели и доли', cell: row => <ul className="shares">{row.shares.map(share =>
            <li key={share.rightsHolderId}><span>{labels(share.rightsHolderId, holderNames)}</span>
              <strong>{String(share.percentage).replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '').replace('.', ',')} %</strong></li>)}</ul> },
        ]} />
    case 'holders':
      return <DataTable emptyText={emptyText} rowKey={rowKey}
        rows={holders.filter(row => includes(row.name, query)).sort((a, b) => a.name.localeCompare(b.name, 'ru'))}
        columns={[{ label: 'Правообладатель', cell: row => <strong>{row.name}</strong> }]} />
  }
}
