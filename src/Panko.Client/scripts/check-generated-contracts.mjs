import { readdir, readFile } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const generatedDirectory = fileURLToPath(new URL('../src/api-client/', import.meta.url))

async function snapshot(directory) {
  const files = new Map()

  async function visit(current) {
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const path = join(current, entry.name)
      if (entry.isDirectory()) await visit(path)
      else files.set(relative(directory, path), await readFile(path, 'utf8'))
    }
  }

  try {
    await visit(directory)
  } catch (error) {
    if (error.code !== 'ENOENT') throw error
  }
  return files
}

const before = await snapshot(generatedDirectory)
const command = process.platform === 'win32' ? 'npm.cmd' : 'npm'
const result = spawnSync(command, ['run', 'contracts:generate', '--silent'], { stdio: 'inherit' })
if (result.status !== 0) process.exit(result.status ?? 1)

const after = await snapshot(generatedDirectory)
const paths = new Set([...before.keys(), ...after.keys()])
const changed = [...paths].filter((path) => before.get(path) !== after.get(path)).sort()

if (changed.length > 0) {
  console.error(`Generated API contracts were stale: ${changed.join(', ')}`)
  process.exit(1)
}

console.log('Generated API contracts are up to date.')
