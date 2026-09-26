# NeutrinoOS website

A dependency-free static site: package list (from a repository index),
getting-started steps, and documentation links.

## Hosting

Copy this directory anywhere that serves static files:

* **GitHub Pages:** push the directory (or a copy) to the `gh-pages`
  branch / `docs/` folder of a site repository.
* **Netlify / any web server:** publish the folder as-is.

To go live with your own repository listing, edit `config.js`:

```js
var REPO_URL = "https://packages.example.com/repo";
```

The page then renders `repository.json` (name/version/architecture/
description per package). Cross-origin fetch requires the repository
server to send permissive CORS headers — static hosting via GitHub
Pages/Netlify does this out of the box; the bundled
`npkg-repo-server` is meant for devices, not browsers.
