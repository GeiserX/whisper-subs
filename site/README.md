# WhisperSubs documentation site

The published documentation at <https://geiserx.github.io/whisper-subs/>, built with [Docusaurus](https://docusaurus.io/).

## Local development

```bash
npm ci
npm start        # dev server with hot reload
npm run build    # production build into build/
npm run serve    # serve the production build locally
```

`npm run serve` is the honest check: the site is published under the `/whisper-subs/` base path, and `npm start` does not exercise that.

## Deployment

Deployment is automatic and there is no manual step. **Do not run `npm run deploy`** — it pushes to a `gh-pages` branch, which this repository does not use, and it would not publish anything.

`.github/workflows/pages.yml` is the only thing that publishes to GitHub Pages. It builds this site, adds the Jellyfin plugin catalog (`manifest.json`) to the output, and uploads the result as a single Pages artifact. It runs on pushes to `main` that touch `site/`, and after a release completes.

Two things about that workflow are load-bearing:

- **A Pages deployment replaces the whole site.** The catalog and the docs have to ship in the same artifact, or publishing one would delete the other. A gate fails the build if the catalog is missing or carries the wrong GUID.
- **The docs build fails soft.** If this site fails to build, the catalog is still published and the run is marked failed afterwards. Missing documentation is recoverable; a missing catalog breaks the plugin repository for every installed user.

## The one URL that cannot move

`docs/setup.md` sets `slug: /docs/setup`. That path is compiled into the plugin's settings page and ships inside released binaries, so installed users request it forever and it cannot be changed for them. Keep the slug, whatever the rest of the structure does.
