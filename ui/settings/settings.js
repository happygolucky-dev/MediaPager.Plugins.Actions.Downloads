const pluginId = 'mediapager.actions.downloads'
const pluginSettingsPath = `/plugins/${encodeURIComponent(pluginId)}/settings`
const catalogFields = [
  { key: 'catalog.movies', label: 'Movies', slug: 'movies' },
  { key: 'catalog.tv-shows', label: 'TV shows', slug: 'tv-shows' },
  { key: 'catalog.music', label: 'Music', slug: 'music' },
  { key: 'catalog.podcasts', label: 'Podcasts', slug: 'podcasts' },
  { key: 'catalog.audiobooks', label: 'Audiobooks', slug: 'audiobooks' },
  { key: 'catalog.books', label: 'Books', slug: 'books' },
]
const defaults = {
  advancedMode: 'false',
  videoProfile: 'CopyToMp4',
  audioProfile: 'AacAudio',
  scratchInCatalog: 'true',
}
const pending = new Map()
let requestNumber = 0
let values = { ...defaults }
let catalogs = []

function request(method, url, body) {
  return new Promise((resolve, reject) => {
    const id = String(++requestNumber)
    const timeout = setTimeout(() => {
      pending.delete(id)
      reject(new Error('The app did not respond in time.'))
    }, 15000)
    pending.set(id, { resolve, reject, timeout })
    parent.postMessage({
      source: 'mediapager-plugin',
      type: 'api',
      id,
      method,
      url,
      body,
    }, '*')
  })
}

window.addEventListener('message', (event) => {
  if (event.source !== parent || event.data?.source !== 'mediapager' || event.data?.type !== 'api-response')
    return
  const waiter = pending.get(String(event.data.id))
  if (!waiter) return
  clearTimeout(waiter.timeout)
  pending.delete(String(event.data.id))
  if (event.data.status < 200 || event.data.status >= 300) {
    waiter.reject(new Error(event.data.body?.detail ?? 'The request failed.'))
    return
  }
  waiter.resolve(event.data.body)
})

function setStatus(text, isError = false) {
  const status = document.getElementById('status')
  status.textContent = text
  status.classList.toggle('error', isError)
}

function renderCatalogFields() {
  const container = document.getElementById('catalogs')
  container.replaceChildren()
  for (const field of catalogFields) {
    const label = document.createElement('label')
    label.className = 'field'
    label.htmlFor = `setting-${field.key}`

    const title = document.createElement('span')
    title.textContent = `${field.label} destination`
    label.append(title)

    const select = document.createElement('select')
    select.id = `setting-${field.key}`
    select.dataset.settingKey = field.key
    const empty = document.createElement('option')
    empty.value = ''
    empty.textContent = `Choose a ${field.label} catalog…`
    select.append(empty)

    const matches = catalogs.filter((catalog) => catalog.catalogType?.slug === field.slug)
    for (const catalog of matches) {
      const option = document.createElement('option')
      option.value = String(catalog.id)
      option.textContent = catalog.name
      select.append(option)
    }
    const selected = values[field.key]
    select.value = selected == null ? '' : String(selected)
    label.append(select)
    container.append(label)
  }

  document.getElementById('catalogs-empty').hidden = catalogs.length > 0
}

function updateMode() {
  const advanced = document.getElementById('advancedMode').checked
  document.getElementById('advancedFields').hidden = !advanced
  document.getElementById('easySummary').hidden = advanced
}

async function load() {
  setStatus('Loading settings…')
  try {
    const [settings, availableCatalogs] = await Promise.all([
      request('GET', pluginSettingsPath),
      request('GET', '/catalogs'),
    ])
    values = { ...defaults, ...(settings?.values ?? {}) }
    catalogs = Array.isArray(availableCatalogs) ? availableCatalogs : []
    renderCatalogFields()
    document.getElementById('advancedMode').checked = String(values.advancedMode).toLowerCase() === 'true'
    document.getElementById('videoProfile').value = values.videoProfile || defaults.videoProfile
    document.getElementById('audioProfile').value = values.audioProfile || defaults.audioProfile
    document.getElementById('scratchInCatalog').checked = String(values.scratchInCatalog).toLowerCase() !== 'false'
    updateMode()
    setStatus('')
  } catch (error) {
    setStatus(error.message || 'Could not load Downloads settings.', true)
  }
}

async function save() {
  const button = document.getElementById('save')
  button.disabled = true
  setStatus('Saving…')
  const payload = {
    advancedMode: String(document.getElementById('advancedMode').checked),
    videoProfile: document.getElementById('videoProfile').value || defaults.videoProfile,
    audioProfile: document.getElementById('audioProfile').value || defaults.audioProfile,
    scratchInCatalog: String(document.getElementById('scratchInCatalog').checked),
  }
  for (const field of catalogFields) {
    const select = document.getElementById(`setting-${field.key}`)
    payload[field.key] = select.value || null
  }

  try {
    await request('PUT', pluginSettingsPath, payload)
    values = { ...values, ...payload }
    setStatus('Settings saved.')
  } catch (error) {
    setStatus(error.message || 'Could not save Downloads settings.', true)
  } finally {
    button.disabled = false
  }
}

document.getElementById('advancedMode').addEventListener('change', updateMode)
document.getElementById('save').addEventListener('click', save)
load()
parent.postMessage({ source: 'mediapager-plugin', type: 'ready' }, '*')
