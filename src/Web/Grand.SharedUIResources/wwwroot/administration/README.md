# Admin vendored front-end libraries

The admin panel's JavaScript and CSS are **vendored**: the files in this directory
are the artifact, committed directly and served as-is. There is no build step and
no `node_modules` - unlike the storefront, which builds from
`src/Web/Grand.Web/vueapp/`.

That left these libraries with no version record, so nothing could tell you whether
any of them carried a known vulnerability. `package.json` here exists to close that
gap.

## What this manifest is, and is not

**It is an audit manifest.** It pins the versions actually vendored, so that
`npm audit` and `npm outdated` can report against them:

```bash
cd src/Web/Grand.SharedUIResources/wwwroot/administration
npm install --package-lock-only    # resolve, without downloading
npm audit
```

**It is not a build input.** Nothing installs from it and no file here is produced
from `node_modules`. Replacing a vendored file means updating its version in
`package.json` by hand - the two can drift, and only discipline keeps them aligned.

Wiring the copy step so the manifest becomes the source of truth is the obvious
follow-up. It is a larger change: several of these libraries are patched in place
or ship non-standard builds.

## Versions that could not be determined

Left out of `package.json` deliberately, because a wrong version is worse than an
absent one:

| Library | Why |
|---|---|
| `jquery.validate.min.js` | Ships as a NuGet-licensed Microsoft redistribution with no version string |
| `jquery.tmpl.min.js` | No version header; the project has been unmaintained since 2011 |
| `simple-line-icons` | No version in the CSS |
| `tagEditor` (1.3.3) | Not published to npm under a resolvable name |
| `farbtastic` (1.2) | Not published to npm |
| `fineuploader` (4.2.2) | Predates the `fine-uploader` npm package, which starts at 5.x - the version does not resolve |
| `elfinder` (2.1.59) | npm package name differs from the vendored layout |
| `roxy_fileman` | Not published to npm |
| `kendo` (2021.3.914) | Commercial; `@progress/kendo-ui` requires a licensed registry |
| `build/` | Colorlib admin template (see `License.txt`), not a package |

## Known issues this surfaced

**jQuery 2.2.4** (`build/js/jquery.min.js`) is the version the admin actually
loads. It is affected by CVE-2020-11022 and CVE-2020-11023 - cross-site scripting
via `.html()`, `.append()` and similar when passed untrusted markup - both fixed in
jQuery 3.5.0. The admin panel accepts HTML in several places (product descriptions,
page content, message templates), so this is reachable rather than theoretical.

**`roxy_fileman/js/` bundles three further jQuery copies** - 1.10.2, 1.11.1 and
2.1.1 - all affected by the same advisories and all older still. They are not
referenced by the admin layout, so the practical fix is to delete them rather than
upgrade them.

**Bootstrap 4.5.2** here versus **5.3.3** in the storefront: two major versions
apart, in one application.

**Kendo UI 2021.3.914** is the backbone of the admin - 240 `kendoGrid`
instantiations across 163 views - and is several years behind.

None of these are fixed by this manifest. It exists so they are visible.
