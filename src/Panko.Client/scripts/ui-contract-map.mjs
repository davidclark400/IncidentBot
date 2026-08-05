import { createHash } from 'node:crypto'
import { access, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const clientRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const mapPath = resolve(clientRoot, 'ui-contract-map.json')
const update = process.argv.includes('--update')

const map = JSON.parse(await readFile(mapPath, 'utf8'))
const specPath = resolve(clientRoot, map.openApi)
const spec = JSON.parse(await readFile(specPath, 'utf8'))
const errors = []
const stale = []

function normalize(value) {
  if (Array.isArray(value)) {
    const normalized = value.map(normalize)
    return normalized.every((item) => item === null || ['boolean', 'number', 'string'].includes(typeof item))
      ? normalized.toSorted((left, right) => JSON.stringify(left).localeCompare(JSON.stringify(right)))
      : normalized
  }
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .filter((key) => !['description', 'examples', 'externalDocs', 'summary', 'tags', 'title'].includes(key))
        .sort()
        .map((key) => [key, normalize(value[key])]),
    )
  }
  return value
}

function contractDigest(value) {
  return `sha256:${createHash('sha256').update(JSON.stringify(normalize(value))).digest('hex')}`
}

function findOperation(operationId) {
  for (const [path, pathItem] of Object.entries(spec.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (operation?.operationId === operationId) return { method: method.toUpperCase(), path, operation }
    }
  }
  return null
}

function collectSchemaReferences(value, references = new Set()) {
  if (Array.isArray(value)) {
    for (const item of value) collectSchemaReferences(item, references)
  } else if (value && typeof value === 'object') {
    for (const [key, item] of Object.entries(value)) {
      if (key === '$ref' && typeof item === 'string' && item.startsWith('#/components/schemas/')) {
        references.add(item.slice('#/components/schemas/'.length))
      } else {
        collectSchemaReferences(item, references)
      }
    }
  }
  return references
}

async function validateConsumers(consumers, owner) {
  if (!Array.isArray(consumers) || consumers.length === 0) {
    errors.push(`${owner} has no UI consumers.`)
    return
  }
  for (const consumer of consumers) {
    if (!consumer.name || !consumer.file) {
      errors.push(`${owner} has a consumer without name/file.`)
      continue
    }
    const sourcePath = resolve(clientRoot, consumer.file)
    try {
      await access(sourcePath)
      const source = await readFile(sourcePath, 'utf8')
      if (!source.includes(consumer.name)) {
        errors.push(`${owner} maps to ${consumer.name}, but that name was not found in ${consumer.file}.`)
      }
    } catch {
      errors.push(`${owner} maps to missing file ${consumer.file}.`)
    }
  }
}

function reviewLines(binding) {
  return binding.consumers.map((consumer) => {
    const detail = consumer.fields?.length
      ? ` [${consumer.fields.join(', ')}]`
      : consumer.behavior
        ? ` — ${consumer.behavior}`
        : ''
    return `    - ${consumer.name} (${consumer.file})${detail}`
  })
}

const mappedSchemaNames = new Set((map.schemas ?? []).map((binding) => binding.schema))
const seenSchemas = new Set()
for (const binding of map.schemas ?? []) {
  const owner = `schema ${binding.schema}`
  if (seenSchemas.has(binding.schema)) errors.push(`${owner} is mapped more than once.`)
  seenSchemas.add(binding.schema)

  const schema = spec.components?.schemas?.[binding.schema]
  if (!schema) {
    errors.push(`${owner} does not exist in ${map.openApi}.`)
    continue
  }

  await validateConsumers(binding.consumers, owner)
  const properties = Object.keys(schema.properties ?? {})
  const usedFields = new Set(binding.consumers.flatMap((consumer) => consumer.fields ?? []))
  const ignoredFields = new Set(Object.keys(binding.ignoredFields ?? {}))

  for (const consumer of binding.consumers) {
    for (const field of consumer.fields ?? []) {
      if (!properties.includes(field)) errors.push(`${owner}.${field} is mapped to ${consumer.name}, but no longer exists.`)
      for (const reference of collectSchemaReferences(schema.properties?.[field])) {
        if (!mappedSchemaNames.has(reference)) {
          errors.push(`${owner}.${field}, consumed by ${consumer.name}, references unmapped schema ${reference}.`)
        }
      }
    }
  }
  for (const field of usedFields) {
    if (ignoredFields.has(field)) errors.push(`${owner}.${field} is both consumed and ignored.`)
  }
  for (const field of ignoredFields) {
    if (!properties.includes(field)) errors.push(`${owner} ignores missing field ${field}.`)
    if (!binding.ignoredFields[field]) errors.push(`${owner}.${field} needs a reason for being ignored.`)
  }
  for (const field of properties) {
    if (!usedFields.has(field) && !ignoredFields.has(field)) {
      errors.push(`${owner}.${field} is new or unmapped. Review: ${binding.consumers.map((consumer) => consumer.name).join(', ')}.`)
    }
  }

  const actual = contractDigest(schema)
  if (binding.contractHash !== actual) {
    stale.push([`${owner} changed. Review:`, ...reviewLines(binding)])
    if (update) binding.contractHash = actual
  }
}

const seenOperations = new Set()
for (const binding of map.operations ?? []) {
  const owner = `operation ${binding.operationId}`
  if (seenOperations.has(binding.operationId)) errors.push(`${owner} is mapped more than once.`)
  seenOperations.add(binding.operationId)

  const found = findOperation(binding.operationId)
  if (!found) {
    errors.push(`${owner} does not exist in ${map.openApi}.`)
    continue
  }

  await validateConsumers(binding.consumers, owner)
  const operationReferences = collectSchemaReferences(found.operation)
  const ignoredSchemas = new Set(Object.keys(binding.ignoredSchemas ?? {}))
  for (const reference of operationReferences) {
    if (!mappedSchemaNames.has(reference) && !ignoredSchemas.has(reference)) {
      errors.push(`${owner} references unmapped response/request schema ${reference}.`)
    }
  }
  for (const reference of ignoredSchemas) {
    if (!operationReferences.has(reference)) errors.push(`${owner} ignores schema ${reference}, but no longer references it.`)
    if (!binding.ignoredSchemas[reference]) errors.push(`${owner} needs a reason for ignoring schema ${reference}.`)
  }
  const actual = contractDigest(found)
  if (binding.contractHash !== actual) {
    stale.push([`${owner} (${found.method} ${found.path}) changed. Review:`, ...reviewLines(binding)])
    if (update) binding.contractHash = actual
  }
}

if (errors.length > 0) {
  console.error('UI contract map is invalid:')
  for (const error of errors) console.error(`  - ${error}`)
  console.error('Update ui-contract-map.json after reviewing the named UI consumers.')
  process.exit(1)
}

if (update) {
  await writeFile(mapPath, `${JSON.stringify(map, null, 2)}\n`)
  console.log(`Updated ${map.schemas.length} schema and ${map.operations.length} operation digests.`)
  process.exit(0)
}

if (stale.length > 0) {
  console.error('OpenAPI changed in areas consumed by the UI:')
  for (const lines of stale) console.error(lines.join('\n'))
  console.error('Review/update the listed UI, then run: npm run ui-contracts:update')
  process.exit(1)
}

console.log(`UI contract map is current (${map.schemas.length} schemas, ${map.operations.length} operations).`)
