// Checks the Report button script against the oldest browser it supports: Chromium 38, as
// in webOS 3. Fails on JavaScript syntax newer than ES5, on a global, static member or
// method that Chromium 38 lacks (MDN's browser-compat-data), and on CSS it lacks (doiuse),
// naming each feature and its line.
//
//   node scripts/compat/check.mjs [file]   (default: the plugin's reporter.js)
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import * as espree from 'espree';
import bcd from '@mdn/browser-compat-data' with { type: 'json' };
import postcss from 'postcss';
import doiuse from 'doiuse';

const CHROME = 38;
const BROWSERS = ['chrome 38'];
// jellyfin-web's legacy polyfills (src/lib/legacy: core-js and whatwg-fetch) give every client it
// supports Promise and fetch, so ApiClient's answers carry Response methods such as json().
const POLYFILLED = new Set(['Promise', 'fetch']);
const POLYFILLED_METHODS = new Set(['json', 'text']);
// CSS that is a progressive enhancement: allowed only when the sheet also has the fallback
// Chromium 38 uses, so the old browser still gets the behaviour.
const ENHANCEMENTS = {
    'css-focus-visible': { fallback: /:focus:not\(:focus-visible\)/, why: 'a :focus outline that :focus:not(:focus-visible) removes again' },
};

const file = process.argv[2] ?? fileURLToPath(new URL('../../src/Jellyfin.Plugin.MediaReports/Web/reporter.js', import.meta.url));
const code = readFileSync(file, 'utf8');
const problems = [];

// ---- JavaScript syntax --------------------------------------------------------------------
let ast;
try {
    ast = espree.parse(code, { ecmaVersion: 5, sourceType: 'script', loc: true });
} catch (e) {
    console.error(`FAIL: ${file}:${e.lineNumber}: syntax newer than ES5: ${e.message}`);
    process.exit(1);
}

// ---- Browser support from browser-compat-data ---------------------------------------------
function chromeAdded(entry) {
    const support = entry?.__compat?.support?.chrome;
    if (!support) {
        return null;
    }
    const first = [].concat(support).find((s) => !s.prefix && !s.alternative_name && !s.flags) ?? null;
    if (!first || first.version_added === false || first.version_added === null) {
        return Infinity;
    }
    if (first.version_added === true) {
        return 0;
    }
    return parseFloat(String(first.version_added).replace(/^≤/, ''));
}

function topLevel(name) {
    return bcd.api[name] ?? bcd.javascript.builtins[name] ?? null;
}

// Every interface or builtin that has a member of this name, with the Chrome version it came in.
const members = new Map();
for (const group of [bcd.api, bcd.javascript.builtins]) {
    for (const [owner, entry] of Object.entries(group)) {
        for (const [member, data] of Object.entries(entry)) {
            if (member === '__compat') {
                continue;
            }
            const added = chromeAdded(data);
            if (added !== null) {
                (members.get(member) ?? members.set(member, []).get(member)).push({ owner, added });
            }
        }
    }
}

const declared = new Set();
// Methods the script defines itself, as in `{ run: function () {} }`, are its own, whatever newer
// web API shares the name.
const ownMethods = new Set();
function walk(node, parent, visit) {
    if (!node || typeof node.type !== 'string') {
        return;
    }
    visit(node, parent);
    for (const [key, value] of Object.entries(node)) {
        if (key === 'loc' || key === 'range') {
            continue;
        }
        if (Array.isArray(value)) {
            value.forEach((child) => walk(child, node, visit));
        } else if (value && typeof value.type === 'string') {
            walk(value, node, visit);
        }
    }
}

walk(ast, null, (node) => {
    if (node.type === 'VariableDeclarator' && node.id.type === 'Identifier') {
        declared.add(node.id.name);
    }
    if (node.type === 'FunctionDeclaration' && node.id) {
        declared.add(node.id.name);
    }
    if ((node.type === 'FunctionDeclaration' || node.type === 'FunctionExpression')) {
        node.params.forEach((p) => p.type === 'Identifier' && declared.add(p.name));
    }
    if (node.type === 'CatchClause' && node.param?.type === 'Identifier') {
        declared.add(node.param.name);
    }
    if (node.type === 'Property' && node.key.type === 'Identifier' && node.value.type === 'FunctionExpression') {
        ownMethods.add(node.key.name);
    }
    if (node.type === 'AssignmentExpression' && node.left.type === 'MemberExpression' && !node.left.computed
        && node.right.type === 'FunctionExpression') {
        ownMethods.add(node.left.property.name);
    }
});

function report(node, what) {
    problems.push(`${file}:${node.loc.start.line}: ${what}`);
}

walk(ast, null, (node, parent) => {
    // A global used by name, as in `new IntersectionObserver(...)` or `fetch(...)`.
    if (node.type === 'Identifier' && !declared.has(node.name) && !POLYFILLED.has(node.name)
        && !(parent?.type === 'MemberExpression' && parent.property === node && !parent.computed)
        && !(parent?.type === 'Property' && parent.key === node)) {
        const added = chromeAdded(topLevel(node.name));
        if (added !== null && added > CHROME) {
            report(node, `${node.name} needs Chrome ${added === Infinity ? '(not supported)' : added}`);
        }
    }
    if (node.type !== 'MemberExpression' || node.computed || node.property.type !== 'Identifier') {
        return;
    }
    const name = node.property.name;
    // A static member of a global, as in `Object.assign` or `Array.from`.
    if (node.object.type === 'Identifier' && !declared.has(node.object.name) && !POLYFILLED.has(node.object.name)) {
        const owner = topLevel(node.object.name);
        if (owner && owner[name]) {
            const added = chromeAdded(owner[name]);
            if (added !== null && added > CHROME) {
                report(node, `${node.object.name}.${name} needs Chrome ${added}`);
            }
            return;
        }
    }
    // A method called on any object, as in `el.closest(...)`: flagged only when every interface
    // that has a member of that name came after Chromium 38, so a common name is never blamed for
    // another interface's newer member. Property reads are not checked: plain objects of the
    // script's own share names with newer APIs (state.loading, request.dataType).
    if (parent?.type !== 'CallExpression' || parent.callee !== node || ownMethods.has(name) || POLYFILLED_METHODS.has(name)) {
        return;
    }
    const known = members.get(name);
    if (known && known.length > 0 && known.every((m) => m.added > CHROME)) {
        const first = Math.min(...known.map((m) => m.added));
        report(node, `.${name} needs Chrome ${first === Infinity ? '(not supported)' : first} (${[...new Set(known.map((m) => m.owner))].slice(0, 3).join(', ')})`);
    }
});

// ---- CSS -------------------------------------------------------------------------------------
// The script's CSS: the STYLES sheet, inline style attributes, and element.style assignments.
function literal(node) {
    if (node.type === 'Literal' && typeof node.value === 'string') {
        return node.value;
    }
    if (node.type === 'BinaryExpression' && node.operator === '+') {
        const left = literal(node.left);
        const right = literal(node.right);
        return left === null || right === null ? null : left + right;
    }
    return null;
}

const sheets = [];
walk(ast, null, (node) => {
    if (node.type === 'VariableDeclarator' && node.id.name === 'STYLES' && node.init) {
        sheets.push({ css: literal(node.init) ?? '', line: node.loc.start.line });
    }
    if (node.type === 'CallExpression' && node.callee.type === 'MemberExpression' && node.callee.property.name === 'setAttribute'
        && node.arguments[0]?.value === 'style') {
        const text = literal(node.arguments[1]);
        if (text !== null) {
            sheets.push({ css: `x{${text}}`, line: node.loc.start.line });
        }
    }
    if (node.type === 'AssignmentExpression' && node.left.type === 'MemberExpression' && !node.left.computed
        && node.left.object.type === 'MemberExpression' && node.left.object.property.name === 'style') {
        const value = literal(node.right);
        if (value !== null && value !== '') {
            const property = node.left.property.name.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`);
            sheets.push({ css: `x{${property}:${value}}`, line: node.loc.start.line });
        }
    }
});

for (const sheet of sheets) {
    const found = [];
    await postcss([doiuse({ browsers: BROWSERS, onFeatureUsage: (usage) => found.push(usage) })])
        .process(sheet.css, { from: undefined });
    for (const usage of found) {
        // doiuse marks every overflow as partial; only overflow: clip and the two-value form are
        // newer than Chromium 38.
        if (usage.feature === 'css-overflow' && !/overflow(-[xy])?\s*:\s*(clip|\S+\s+\S)/.test(usage.usage?.toString() ?? '')) {
            continue;
        }
        const enhancement = ENHANCEMENTS[usage.feature];
        if (enhancement && enhancement.fallback.test(sheet.css)) {
            continue;
        }
        const where = usage.usage?.source?.start;
        const snippet = usage.usage?.toString().slice(0, 60);
        problems.push(`${file}:${sheet.line}: CSS ${usage.feature} (${usage.featureData?.title ?? ''}) not in Chrome 38: ${snippet}${where && sheet.css.length > 200 ? ` [sheet line ${where.line}]` : ''}`);
    }
}

if (problems.length > 0) {
    for (const p of problems) {
        console.error(`FAIL: ${p}`);
    }
    console.error(`FAIL: ${problems.length} feature(s) Chromium 38 lacks`);
    process.exit(1);
}
console.log(`PASS: ${file} uses only ES5 syntax, web APIs and CSS that Chromium 38 supports (${sheets.length} style blocks checked)`);
