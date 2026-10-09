// Parse the shipped Postman collections with the official postman-collection
// SDK (the library Postman itself uses) to prove they import cleanly.
// Setup: npm i postman-collection   (or use the fallback path below)
// Run:   node build/pm-parse-test.js
const fs = require('fs');
let postmanCollection;
try {
  postmanCollection = require('postman-collection');
} catch {
  postmanCollection = require('/tmp/pmtest/node_modules/postman-collection');
}
const { Collection } = postmanCollection;

const files = [
  '/mnt/d/Projects/Code/NeutrinoOS/docs/samples/webapi/NeutrinoWebApi.postman_collection.json',
  '/mnt/d/Projects/Code/NeutrinoOS/docs/samples/rest-api/NeutrinoSampleApi.postman_collection.json',
];

let failed = false;
for (const p of files) {
  try {
    const c = new Collection(JSON.parse(fs.readFileSync(p, 'utf8')));
    let requests = 0;
    c.forEachItem(() => { requests++; });
    console.log('PARSED OK:', c.name, '| items:', c.items.count(), '| requests:', requests);
  } catch (e) {
    failed = true;
    console.log('PARSE FAILED:', p);
    console.log('  ', e && e.message);
  }
}
process.exit(failed ? 1 : 0);
