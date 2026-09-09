/*
 * Writes wwwroot/bundles/asset-manifest.json - the integrity hashes for the built
 * bundles.
 *
 * Subresource Integrity only means something when the hash reaches the browser by a
 * path the file's own host cannot write. Serve bundles from a CDN and publish these
 * hashes through the application (they are stored in settings, in the database), and
 * a compromised storage account can replace a bundle but cannot make the browser run
 * it - the hash will not match and the script is blocked.
 *
 * Publish both from the same place and SRI protects nothing: an attacker changes the
 * file and the hash together. That separation is the whole point, so this file is an
 * input to the deployment, not something uploaded alongside the bundles.
 */
import { createHash } from 'node:crypto'
import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { fileURLToPath, URL } from 'node:url'

const resolve = p => fileURLToPath(new URL(p, import.meta.url))
const bundles = resolve('../wwwroot/bundles/')

// logical name -> file on disk. The logical name is what a Razor view asks for, so
// the physical file can change without touching a view.
const assets = [
    'app.runtime.bundle.js',
    'libs.css',
    'style.min.css',
    'style.rtl.min.css'
]

const sri = file =>
    'sha384-' + createHash('sha384').update(readFileSync(file)).digest('base64')

// No timestamp: the manifest is committed and Frontend CI rebuilds it to check the
// committed bundles still match their source. A generation time would differ on every
// run and fail that check permanently. The release is identified by the version prefix
// its files are uploaded under, not by anything in here.
const manifest = { version: 1, assets: {} }

for (const name of assets) {
    const path = bundles + name
    if (!existsSync(path)) {
        console.error(`  missing ${name} - run the bundle build first`)
        process.exit(1)
    }
    manifest.assets[name] = { file: name, integrity: sri(path) }
}

writeFileSync(bundles + 'asset-manifest.json', JSON.stringify(manifest, null, 2) + '\n')
console.log(`  asset-manifest.json (${assets.length} assets)`)
