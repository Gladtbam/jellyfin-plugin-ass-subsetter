import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const defaultTemplate = '{family}{styleSuffix}{collectionSuffix}{versionSuffix}';
const catalog = [{"name":"family","missingBehavior":"skip","isSuffix":false},{"name":"style","missingBehavior":"empty","isSuffix":false},{"name":"fullName","missingBehavior":"skip","isSuffix":false},{"name":"postscript","missingBehavior":"skip","isSuffix":false},{"name":"manufacturer","missingBehavior":"empty","isSuffix":false},{"name":"version","missingBehavior":"empty","isSuffix":false},{"name":"legacyFamily","missingBehavior":"skip","isSuffix":false},{"name":"legacyStyle","missingBehavior":"empty","isSuffix":false},{"name":"typographicFamily","missingBehavior":"skip","isSuffix":false},{"name":"typographicStyle","missingBehavior":"empty","isSuffix":false},{"name":"styleSuffix","missingBehavior":"empty","isSuffix":true},{"name":"versionSuffix","missingBehavior":"empty","isSuffix":true},{"name":"collectionSuffix","missingBehavior":"collection-family-required","isSuffix":true},{"name":"originalName","missingBehavior":"skip","isSuffix":false},{"name":"faceCount","missingBehavior":"skip","isSuffix":false},{"name":"fingerprint","missingBehavior":"skip","isSuffix":false}];

const script = await readFile(new URL('../../Jellyfin.Plugin.AssSubsetter/Configuration/configPage.js', import.meta.url), 'utf8');

class Element {
    constructor(tag = 'div') { this.tagName = tag; this.children = []; this.handlers = new Map(); this.attributes = new Map(); this.dataset = {}; this.style = {}; this.value = ''; this.textContent = ''; }
    get childNodes() { return this.children; }
    get firstChild() { return this.children[0]; }
    get options() { return this.children; }
    appendChild(child) { child.parentElement = this; this.children.push(child); if (this.tagName === 'select' && !this.value) this.value = child.value; return child; }
    removeChild(child) { this.children.splice(this.children.indexOf(child), 1); if (!this.children.length) this.value = ''; }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name); }
    addEventListener(name, callback) { this.handlers.set(name, callback); }
    closest(selector) {
        const names = selector.split(',').map(name => name.trim());
        for (let element = this; element; element = element.parentElement) {
            if (names.includes(element.tagName)) return element;
        }
        return null;
    }
    fire(name, options = {}) {
        const event = { target: this, preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; }, ...options };
        let result;
        for (let element = this; element; element = ['click', 'keydown'].includes(name) ? element.parentElement : null) {
            if (element.handlers.has(name)) result = element.handlers.get(name)(event);
            if (event.stopped) break;
        }
        return result;
    }
    focus() { this.focused = true; }
    setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
    set innerHTML(_) { throw new Error('Dynamic data must not be interpreted as HTML'); }
}

function page(extraRoutes = {}) {
    const elements = new Map();
    const gets = [];
    const posts = [];
    const queries = [];
    const updates = [];
    const authorizations = [];
    const timers = new Map();
    const fetches = [];
    let nextTimer = 0;
    const config = extraRoutes.config || { FontFileNameTemplate: '{fullName}', FontFolderTemplate: '', RemoveEmptyFontDirectories: false };
    const hostile = '<img src=x onerror=alert(1)>';
    const routes = {
        'rename/items': { canRefreshCandidates: true, candidateState: { status: 'ready', generatedUtc: '2026-10-08T00:00:00Z' }, current: { generationId: 3, analysisBatchId: 2, ruleFingerprint: 'rules', rules: { fileNameTemplate: defaultTemplate, folderTemplate: '', removeEmptyDirectories: false } }, blockReason: null, items: [

            { fileId: 7, oldPath: hostile, newPath: 'Example-Regular-v2.003-aabbccdd.ttf', sha256: 'a'.repeat(64), sourceRoot: '/fonts', namingEvidence: { fields: [{ name: 'postscript', nameId: 6, faceIndex: 0, source: 'Windows English', value: 'Example-Regular' }, { name: 'style', nameId: 2, faceIndex: 0, source: 'Windows English', value: 'Regular' }, { name: 'version', faceIndex: 0, source: 'inventory-face', value: '2.003' }], selections: [{ faceIndex: 0, kind: 'legacy-pair', detail: null }] }, collisionFingerprint: 'aabbccdd', status: 'Pending', error: null },
            { fileId: 8, oldPath: 'protected.ttf', newPath: null, sha256: 'b'.repeat(64), status: 'Skipped', error: 'protected directory' }
        ].map((item, index) => ({ key: 'current:' + index, generationId: 3, fileId: item.fileId, canExecute: index === 0, canRollback: false, maintenanceBatchId: null, operationRequestId: null, item })), totalCount: 2, page: 1, pageSize: 50 }

    };
    Object.assign(routes, extraRoutes);
    const document = {
        querySelector(selector) { if (!elements.has(selector)) elements.set(selector, new Element((selector === '#AnalysisBatchSelect' || selector.endsWith('PageSelect')) ? 'select' : 'div')); return elements.get(selector); },
        querySelectorAll(selector) {
            const attribute = selector.match(/^\[(data-report-[\w-]+)\]$/)?.[1];
            return attribute ? [...elements.values()].flatMap(descendants).filter(element => element.attributes.has(attribute)) : [];
        },
        createElement(tag) { return new Element(tag); }
    };
    const context = vm.createContext({ document, Headers, AbortController, window: {
        crypto: { randomUUID: () => 'retry-key' },
        setTimeout: callback => { const id = ++nextTimer; timers.set(id, callback); return id; },
        clearTimeout: id => timers.delete(id),
        fetch: async (url, options) => {
            fetches.push({ path: url, options });
            if (options.method === 'GET') {
                gets.push(url);
                assert.ok(url in routes, 'Unexpected read: ' + url);
                const result = typeof routes[url] === 'function' ? routes[url](queries.at(-1).query) : routes[url];
                return { ok: true, json: async () => result };
            }
            posts.push({ path: url, body: JSON.parse(options.body) });
            authorizations.push(options.headers.get('Authorization'));
            const supplied = extraRoutes['POST ' + url];
            const result = typeof supplied === 'function' ? supplied() : supplied;
            return { ok: true, json: async () => url === 'rename/templates/validate'
                ? { errors: [], defaultFileNameTemplate: defaultTemplate, formatVersion: 2, fields: catalog, ...result }
                : result || { id: 99, status: 2 } };
        }
    }, ApiClient: {
        getUrl: (path, query) => { const route = path.replace('AssSubsetter/Fonts/', ''); queries.push({ route, query }); return route; },
        getJSON: async path => { gets.push(path); assert.ok(path in routes, `Unexpected read: ${path}`); return typeof routes[path] === "function" ? routes[path](queries.at(-1).query) : routes[path]; },
        getPluginConfiguration: async () => ({ ...config }),
        getServerConfiguration: async () => ({ UICulture: 'en' }),
        updatePluginConfiguration: async (_, value) => { updates.push(value); return {}; },
        accessToken: () => 'test-token'
    } });
    context.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
    vm.runInContext(script, context);
    return { context, document, gets, posts, queries, hostile, updates, config, authorizations, routes, timers, fetches,
        async tick() { const [id, callback] = timers.entries().next().value; timers.delete(id); await callback(); } };
}

function descendants(element) {
    return [element, ...element.children.flatMap(descendants)];
}

function allText(element) {
    return [element.textContent, ...element.children.map(allText)].join(' ');
}

test('overview renders generation, health, eligible file count, reclaimed bytes, and phase timings', async () => {
    const p = page({ overview: {
        publishedGenerationId: 7, publishedFileCount: 3, publishedFaceCount: 4,
        latestAnalysis: { id: 9, candidateCount: 2, groupCount: 1 },
        eligibleQuarantineCount: 2, reclaimableBytes: 2048,
        databaseHealth: { schemaVersion: 5, journalMode: 'wal', size: 4096 },
        phaseTimings: [{ phase: 'parsing', duration: '00:00:00.025' }]
    } });
    await p.context.loadFontOverview();
    const values = descendants(p.document.querySelector('#FontOverviewValues')).map(child => String(child.textContent));
    assert.ok(values.includes('7'));
    assert.ok(values.includes('2'));
    assert.ok(values.includes('2.0 KiB'));
    assert.ok(allText(p.document.querySelector('#FontDatabaseValues')).includes('wal'));
    assert.ok(allText(p.document.querySelector('#FontPerformanceRows')).includes('parsing'));
});

test('quarantine shows status views across batches and restores each batch selection', async () => {
    const p = page({ 'quarantine/items': {
        items: [
            { id: 11, maintenanceBatchId: 4, fileId: 7, status: 2, sourcePath: 'C:\\Fonts\\one.ttf', quarantinePath: 'q/one.ttf' },
            { id: 12, maintenanceBatchId: 5, fileId: 8, status: 2, sourcePath: 'two.ttf', quarantinePath: 'q/two.ttf' }
        ], totalCount: 2, page: 1, pageSize: 50
    } });

    await p.context.loadFontQuarantine();

    assert.deepEqual(p.gets, ['quarantine/items']);
    assert.equal(p.queries[0].query.status, 'quarantined');
    assert.equal(p.document.querySelector('#FontQuarantineRows').children.length, 2);
    assert.equal(p.document.querySelector('#FontQuarantineRows').children[0].children[0].children[1].textContent, 'one.ttf');
    for (const card of p.document.querySelector('#FontQuarantineRows').children) {
        const checkbox = card.children[0].children[0];
        checkbox.checked = true;
        checkbox.fire('change');
    }
    p.document.querySelector('#RestoreSelectedButton').fire('click');
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(p.posts.map(post => ({ path: post.path, batchId: post.body.batchId, selectedFileIds: post.body.selectedFileIds })), [
        { path: 'operations/restore', batchId: 4, selectedFileIds: [7] },
        { path: 'operations/restore', batchId: 5, selectedFileIds: [8] }
    ]);

    p.document.querySelector('#QuarantineFailedTab').fire('click');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(p.queries.at(-1).query.status, 'failed');
    assert.ok(!p.gets.some(path => path.startsWith('quarantine/batches')));
});
test('analysis reports send every exact filter with bounded paging and render paths as text', async () => {
    const p = page({ reports: { items: [{ analysisBatchId: 7, relationshipKind: 2, action: 1, paths: ['<img src=x onerror=alert(1)>'], directories: ['/custom'], technologies: ['TrueType'], isUnclassifiable: false }], totalCount: 1, page: 1 } });
    p.context.fontManagementState.analysisBatchId = 7;
    p.context.fontManagementState.pageSize = 999;
    const values = { '#ReportRelationshipFilter': 'strictFaceDuplicate', '#ReportUnclassifiableFilter': 'exclude', '#ReportDirectoryFilter': '/custom', '#ReportTechnologyFilter': 'TrueType', '#ReportActionFilter': 'quarantineCandidate' };
    for (const [selector, value] of Object.entries(values)) p.document.querySelector(selector).value = value;
    await p.context.loadAnalysisReports();
    const request = p.queries.find(item => item.route === 'reports');
    assert.deepEqual(JSON.parse(JSON.stringify(request.query)), { page: 1, pageSize: 200, analysisBatchId: 7, relationship: 'strictFaceDuplicate', unclassifiable: 'exclude', directory: '/custom', technology: 'TrueType', action: 'quarantineCandidate' });
    assert.ok(allText(p.document.querySelector('#analysis-report-rows')).includes(p.hostile));
});

test('report-only relationship renders directional glyph, score, rationale, and collection evidence as text', async () => {
    const p = page({ reports: { items: [{
        analysisBatchId: 7, relationId: 9, relationshipKind: 7, action: 2,
        paths: ['<img src=x onerror=alert(1)>'], directories: ['/custom'], technologies: ['TrueType'], isUnclassifiable: false,
        pairEvidence: { differingCodePoints: [65], differenceTypes: ['SmallGlyphDifference'], leftCanReplaceRight: false, rightCanReplaceLeft: false, collectionFaceImpact: 'File 1 contains 2 Faces; 1 other Face shares the file' },
        scores: [{ faceId: 10, retentionScore: 15, components: { content: 1, version: 2, technical: 3, names: 4, modified: 5 }, verification: { skia: 1, freetype: 0 } }],
        retainedRationale: ['manual review']
    }], totalCount: 1, page: 1 } });
    p.context.fontManagementState.analysisBatchId = 7;

    await p.context.loadAnalysisReports();

    const row = p.document.querySelector('#analysis-report-rows').children[0];
    const disclosure = descendants(row).find(element => element.tagName === 'details');
    disclosure.open = true;
    disclosure.fire('toggle');
    const rendered = allText(row);
    for (const detail of ['65 (U+0041)', 'No', '2 Faces', '15', '1', '2', '3', '4', '5', 'manual review', p.hostile]) {
        assert.ok(rendered.includes(detail), `Missing report evidence: ${detail}`);
    }
});

test('report evidence only displays supported verification fields', () => {
    const p = page();
    const details = new Element('details');
    p.context.renderReportEvidence(details, {
        scores: [{ faceId: 1, retentionScore: 0, components: {}, verification: { skia: 1, unknownProbe: 1 } }]
    });
    const rendered = allText(details);
    assert.ok(rendered.includes('Skia'));
    assert.ok(!rendered.includes('unknownProbe'));
});

test('group detail renders pair evidence and retained score breakdown', async () => {
    const p = page({ 'analysis-batches/7/groups/5': {
        hardConstraints: ['unique-face-protection'], skipReasons: ['retained for quality'],
        pairs: [{ differingCodePoints: [65], differenceTypes: ['SmallGlyphDifference'], leftCanReplaceRight: false, rightCanReplaceLeft: true, collectionFaceImpact: 'File 1 contains 2 Faces' }],
        group: { members: [{ faceId: 10, retentionScore: 15, score: { content: 1, version: 2, technical: 3, names: 4, modified: 5 }, reason: 'retained for quality' }] }
    } });

    const target = new Element();
    assert.equal(await p.context.loadGroupDetail(7, 5, target), true);
    descendants(target).filter(element => element.tagName === 'details').forEach(element => { element.open = true; element.fire('toggle'); });

    const detail = allText(target);
    for (const value of ['65 (U+0041)', 'Yes', '2 Faces', 'Content', 'Version', 'Technical', 'Names', 'Newest modification bonus', '1', '2', '3', '4', '5', 'retained for quality']) {
        assert.ok(detail.includes(value), `Missing group detail: ${value}`);
    }
});

test('current status read preserves source paths, hash, naming evidence and concrete skip reasons as text', async () => {
    const p = page();
    await p.context.loadFontRename();
    assert.deepEqual(p.gets, ['rename/items']);
    const rows = p.document.querySelector('#FontRenameRows').children;
    for (const value of [p.hostile, 'a'.repeat(64), 'postscript', 'Example-Regular', '2.003', 'aabbccdd']) assert.ok(allText(rows[0]).includes(value));
    assert.equal(rows[0].getAttribute('aria-disabled'), 'false');
    assert.equal(rows[1].getAttribute('aria-disabled'), 'true');
    assert.ok(allText(rows[1]).includes('protected directory'));
    assert.deepEqual({ ...p.queries.at(-1).query }, { status: 'pending', page: 1, pageSize: 50 });
});

test('naming evidence groups every field, removes repeated observations, and preserves distinct sources and Faces', () => {
    const p = page();
    const target = new Element();
    const observations = [
        { name: 'collectionSuffix', value: '', source: 'program-suffix' },
        { name: 'postscript', value: p.hostile, source: 'collection-summary', nameId: 6 },
        { name: 'typographicStyle', value: null, source: 'inventory-face', missingReason: 'missing-field', nameId: 17 },
        { name: 'originalName', value: 'Demo', source: 'inventory-file' },
        { name: 'family', value: 'Demo', source: 'inventory-face', nameId: 1, faceIndex: 0 }
    ];
    const fields = [...observations, ...observations.map(field => ({ ...field })),
        { name: 'family', value: 'Demo', source: 'inventory-face', nameId: 1, faceIndex: 1 },
        { name: 'family', value: 'Other', source: 'inventory-face', nameId: 1, faceIndex: 2 },
        { name: 'family', value: 'Demo', source: 'collection-summary', nameId: 1 },
        { name: 'family', value: 'Demo', source: 'inventory-face', nameId: 16, faceIndex: 0 }
    ];
    const original = JSON.stringify(fields);
    p.context.renderNamingEvidence(target, { namingEvidence: { fields, selections: [] } });
    const rows = target.children.filter(row => row.children[0].textContent.startsWith('{'));
    assert.deepEqual(rows.map(row => row.children[0].textContent), ['{collectionSuffix}', '{postscript}', '{typographicStyle}', '{originalName}', '{family}']);
    for (const row of rows.slice(0, 4)) assert.equal(row.children[1].textContent.split('\n\n').length, 1);
    const family = rows.at(-1).children[1].textContent;
    assert.equal(family.split('\n\n').length, 5);
    for (const value of ['FaceIndex 0', 'FaceIndex 1', 'FaceIndex 2', 'Name ID 16', 'Other', 'Collection summary']) {
        assert.ok(family.includes(value), `Missing distinct evidence: ${value}`);
    }
    assert.ok(allText(target).includes(p.hostile));
    assert.equal(JSON.stringify(fields), original, 'Saved evidence must remain intact');
    const empty = new Element();
    p.context.renderNamingEvidence(empty, { namingEvidence: { fields: [
        { name: 'styleSuffix', value: '', source: 'program-suffix' },
        { name: 'style', value: null, source: 'inventory-face', missingReason: 'missing-field', nameId: 2 }
    ] } });
    assert.ok(allText(empty).includes('Empty string'));
    assert.ok(!empty.children.find(row => row.children[0].textContent === '{style}').children[1].textContent.includes('Empty string'));
});

test('selection evidence replaces expression branch positions', () => {
    const p = page();
    p.context.fontManagementState.locale = 'zh';
    const target = new Element();
    const selections = [
        { faceIndex: 0, kind: 'typographic-pair', detail: null },
        { faceIndex: 0, kind: 'typographic-pair', detail: null },
        { faceIndex: 1, kind: 'family-only', detail: 'style=invalid-unicode' },
        { faceIndex: null, kind: 'collection-summary', detail: p.hostile }
    ];
    const original = JSON.stringify(selections);
    p.context.renderNamingEvidence(target, { namingEvidence: { fields: [], selections } });
    const rendered = allText(target);
    for (const value of ['排印名称对', '缺少样式', '集合汇总', 'FaceIndex 1', '无效 Unicode', p.hostile]) assert.ok(rendered.includes(value), value);
    assert.equal((rendered.match(/排印名称对/g) || []).length, 1);
    assert.ok(!rendered.includes('字符位置') && !rendered.includes('分支'));
    assert.equal(JSON.stringify(selections), original);
});


test('normalize submits context and identifiers; rollback groups selected source rows by execution batch', async () => {
    const p = page();
    await p.context.loadFontRename();
    p.context.fontManagementState.renameFileIds.add('current:0');
    await p.document.querySelector('#NormalizeSelectedButton').fire('click');
    assert.deepEqual(p.posts[0], { path: 'operations/normalize-names', body: { generationId: 3, analysisBatchId: 2, ruleFingerprint: 'rules', selectedFileIds: [7], idempotencyKey: 'retry-key' } });
    p.routes['rename/items'] = { current: null, blockReason: 'scan required', items: [22, 23].map(batchId => ({ key: 'batch:' + batchId, maintenanceBatchId: batchId, generationId: 3, fileId: 7, canExecute: false, canRollback: true, item: { fileId: 7, oldPath: 'old.ttf', newPath: 'target.ttf', status: 'Renamed', sha256: 'aa' } })), totalCount: 2 };
    await p.document.querySelector('#RenameStatus-renamed').fire('click');
    p.context.fontManagementState.renameFileIds.add('batch:22');
    p.context.fontManagementState.renameFileIds.add('batch:23');
    await p.document.querySelector('#RollbackSelectedButton').fire('click');
    assert.deepEqual(p.posts.slice(1).map(post => post.body.batchId), [22, 23]);
    assert.ok(p.posts.slice(1).every(post => post.path === 'operations/rename-rollback'));
});

test('replacement report selects whole files and loads group evidence inside the same card', async () => {
    const p = page({
        reports: { items: [{
            analysisBatchId: 7, action: 1, relationshipKind: 1,
            replacement: { groupId: 5, reclaimableBytes: 2048, relationshipKinds: [1], files: [
                { fileId: 1, path: 'keep.ttf', faceCount: 2, decision: 0, reasons: ['retained'] },
                { fileId: 2, path: '<img src=x onerror=alert(1)>', faceCount: 2, decision: 1, reasons: ['replaced'] }
            ] }
        }], totalCount: 1, page: 1 },
        'analysis-batches/7/groups/5': { group: { id: 5, members: [] }, pairs: [], hardConstraints: [], skipReasons: [] }
    });
    p.context.fontManagementState.analysisBatchId = 7;
    await p.context.loadAnalysisReports();
    const card = p.document.querySelector('#analysis-report-rows').children[0];
    const checkboxes = descendants(card).filter(element => element.type === 'checkbox');
    assert.equal(checkboxes.length, 1, 'Only verified quarantine files can be selected');
    assert.ok(allText(card).includes('keep.ttf'));
    assert.ok(allText(card).includes(p.hostile));
    checkboxes[0].checked = true;
    checkboxes[0].fire('change');
    assert.deepEqual(Array.from(p.context.fontManagementState.selectedFileIds), [2]);
    assert.ok(allText(p.document.querySelector('#ReportSelectionStatus')).includes('1'));
    const disclosure = descendants(card).find(element => element.tagName === 'details');
    assert.ok(!p.gets.includes('analysis-batches/7/groups/5'), 'Evidence should load on demand');
    disclosure.open = true;
    disclosure.fire('toggle');
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(p.gets.includes('analysis-batches/7/groups/5'));
    assert.ok(allText(disclosure).includes('5'));
    disclosure.open = false;
    disclosure.fire('toggle');
    disclosure.open = true;
    disclosure.fire('toggle');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(p.gets.filter(path => path === 'analysis-batches/7/groups/5').length, 1);
    p.document.querySelector('#ClearReportSelectionButton').fire('click');
    assert.equal(checkboxes[0].checked, false);
    assert.equal(p.document.querySelector('#QuarantineSelectedButton').disabled, true);
    const selectPlan = descendants(card).find(element => element.attributes.has('data-report-plan-files'));
    selectPlan.fire('click');
    assert.equal(checkboxes[0].checked, true);
    assert.deepEqual(Array.from(p.context.fontManagementState.selectedFileIds), [2]);
    selectPlan.fire('click');
    assert.equal(checkboxes[0].checked, false);
});

test('saves naming rules only after server validation and writes all three settings', async () => {
    const p = page({ 'POST rename/templates/validate': { errors: [{ field: 'fileNameTemplate', code: 'unknown-field', offset: 1 }], defaultFileNameTemplate: defaultTemplate } });
    p.document.querySelector('#txtFontFileNameTemplate').value = '{bad}';
    await p.document.querySelector('#AssSubsetterConfigForm').fire('submit');
    assert.equal(p.updates.length, 0);
    assert.ok(allText(p.document.querySelector('#FontNamingValidation')).includes('Unknown field'));
    const good = page();
    good.document.querySelector('#txtFontFileNameTemplate').value = '{fullName}';
    good.document.querySelector('#txtFontFolderTemplate').value = '{manufacturer}/{family}';
    good.document.querySelector('#chkRemoveEmptyFontDirectories').checked = true;
    await good.document.querySelector('#AssSubsetterConfigForm').fire('submit');
    assert.equal(good.updates.length, 1);
    assert.equal(good.updates[0].FontFileNameTemplate, '{fullName}');
    assert.equal(good.updates[0].FontFolderTemplate, '{manufacturer}/{family}');
    assert.equal(good.updates[0].RemoveEmptyFontDirectories, true);
    assert.deepEqual(good.posts.map(post => post.path), ['rename/templates/validate']);
});

test('reset copies the canonical versioned default without rewriting configured templates on load', async () => {
    const p = page({ 'POST rename/templates/validate': { errors: [], defaultFileNameTemplate: defaultTemplate } });
    await p.document.querySelector('#AssSubsetterConfigPage').fire('pageshow');
    assert.equal(p.document.querySelector('#txtFontFileNameTemplate').value, '{fullName}');
    await p.document.querySelector('#ResetFontNameTemplateButton').fire('click');
    assert.equal(p.document.querySelector('#txtFontFileNameTemplate').value, defaultTemplate);
    assert.equal(p.updates.length, 0);
});

test('blocked current analysis disables execution and clears source selections while retaining history', async () => {
    const p = page();
    await p.context.loadFontRename();
    p.context.fontManagementState.renameFileIds.add('current:0');
    p.routes['rename/items'] = { current: null, blockReason: 'directory policy changed', items: [], totalCount: 0 };
    await p.context.loadFontRename();
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    assert.equal(p.document.querySelector('#NormalizeSelectedButton').disabled, true);
    assert.ok(allText(p.document.querySelector('#RenameContextValidity')).includes('directory policy changed'));
});

test('ordinary reload is read-only', async () => {
    const p = page();
    await p.context.loadFontRename();
    assert.deepEqual(p.posts, []);
    assert.deepEqual(p.gets, ['rename/items']);
});

test('explicit refresh posts durable work, immediately shows pending, and polls ready database pages', async () => {
    const p = page({ 'POST operations/refresh-rename-candidates': { id: 10, status: 0 } });
    p.context.fontManagementState.activeView = 'rename';
    const ready = p.routes['rename/items'];
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'pending', operationRequestId: 10 }, items: [], totalCount: 0 };
    await p.document.querySelector('#RefreshRenameCandidatesButton').fire('click');
    assert.deepEqual(p.posts.map(post => post.path), ['operations/refresh-rename-candidates']);
    assert.equal(p.posts[0].body.idempotencyKey, 'retry-key');
    assert.ok(p.authorizations[0].includes('test-token'));
    assert.match(allText(p.document.querySelector('#RenameContextValidity')), /Waiting/);
    assert.doesNotMatch(allText(p.document.querySelector('#FontRenameState')), /Loading/);
    assert.equal(p.document.querySelector('#NormalizeSelectedButton').disabled, true);
    assert.equal(p.timers.size, 1);
    p.routes['rename/items'] = ready;
    await p.tick();
    assert.equal(p.timers.size, 0);
    assert.equal(p.context.fontManagementState.renameCurrent, true);
    assert.equal(p.gets.filter(path => path === 'rename/items').length, 2);
});

test('running candidates disable selection and failed candidates show reason with explicit retry', async () => {
    const p = page();
    p.context.fontManagementState.activeView = 'rename';
    const ready = p.routes['rename/items'];
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'running' } };
    await p.context.loadFontRename();
    assert.equal(p.document.querySelector('#FontRenameRows').children[0].getAttribute('aria-disabled'), 'true');
    assert.match(allText(p.document.querySelector('#RenameContextValidity')), /Generating/);
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'failed', error: p.hostile } };
    await p.tick();
    assert.match(allText(p.document.querySelector('#RenameContextValidity')), /failed/i);
    assert.ok(allText(p.document.querySelector('#RenameContextValidity')).includes(p.hostile));
    assert.equal(p.document.querySelector('#RefreshRenameCandidatesButton').disabled, false);
    assert.equal(p.timers.size, 0);
});

test('stale configuration response keeps polling through pending to ready', async () => {
    const p = page();
    p.context.fontManagementState.activeView = 'rename';
    const ready = p.routes['rename/items'];
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'stale' }, items: [], totalCount: 0 };
    await p.context.loadFontRename();
    assert.equal(p.timers.size, 1);
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'pending' }, items: [], totalCount: 0 };
    await p.tick();
    assert.equal(p.timers.size, 1);
    p.routes['rename/items'] = ready;
    await p.tick();
    assert.equal(p.timers.size, 0);
    assert.equal(p.context.fontManagementState.renameCurrent, true);
    assert.deepEqual(p.posts, []);
});

test('leaving cancels only the page GET and timer while accepted work persists', async () => {
    const p = page({ 'POST operations/refresh-rename-candidates': { id: 10, status: 0 } });
    p.context.fontManagementState.activeView = 'rename';
    const ready = p.routes['rename/items'];
    p.routes['rename/items'] = { ...ready, current: null, candidateState: { status: 'pending' }, items: [], totalCount: 0 };
    await p.context.queueRenameCandidateRefresh();
    let resolve;
    p.routes['rename/items'] = () => new Promise(done => { resolve = done; });
    const pending = p.context.loadFontRename();
    await new Promise(done => setImmediate(done));
    const request = p.fetches.at(-1);
    await p.document.querySelector('#AssSubsetterConfigPage').fire('pagehide');
    assert.equal(request.options.signal.aborted, true);
    assert.equal(p.timers.size, 0);
    resolve(ready);
    await pending;
    assert.equal(p.context.fontManagementState.renameContext, null);
    assert.deepEqual(p.posts.map(post => post.path), ['operations/refresh-rename-candidates']);
});

test('failed refresh acceptance retries with the original key and keeps generation errors separate', async () => {
    let fail = true;
    const p = page({ 'POST operations/refresh-rename-candidates': () => {
        if (fail) throw new Error('network interrupted');
        return { id: 10, status: 0 };
    } });
    p.context.fontManagementState.activeView = 'rename';
    await p.context.loadFontRename();
    await p.context.queueRenameCandidateRefresh();
    assert.match(allText(p.document.querySelector('#FontOperationStatus')), /network interrupted/);
    assert.equal(p.document.querySelector('#RefreshRenameCandidatesButton').disabled, false);
    assert.equal(p.context.fontManagementState.renameCandidateState.status, 'ready');
    fail = false;
    await p.context.queueRenameCandidateRefresh();
    assert.equal(p.posts[0].body.idempotencyKey, p.posts[1].body.idempotencyKey);
});

test('missing compatible analysis stops polling and disables explicit refresh', async () => {
    const p = page({ 'rename/items': { current: null, candidateState: null, canRefreshCandidates: false, blockReason: 'A complete current font analysis is required.', items: [], totalCount: 0 } });
    p.context.fontManagementState.activeView = 'rename';
    await p.context.loadFontRename();
    assert.equal(p.document.querySelector('#RefreshRenameCandidatesButton').disabled, true);
    assert.equal(p.timers.size, 0);
});

test('directory warning stays separate from successful file outcome and loads only on expansion', async () => {
    const p = page({ 'rename/batches/22/directories': [{ path: '/fonts/old/hidden', status: 'Warning', error: '<img src=x>' }] });
    p.routes['rename/items'].items = [{ key: 'history', maintenanceBatchId: 22, generationId: 3, fileId: 7, canExecute: false, canRollback: true, item: { fileId: 7, oldPath: 'old.ttf', newPath: 'new.ttf', status: 'Renamed', sha256: 'aa' } }];
    await p.document.querySelector('#RenameStatus-renamed').fire('click');
    assert.ok(!p.gets.includes('rename/batches/22/directories'));
    const disclosure = p.document.querySelector('#FontRenameRows').children[0].children.at(-1);
    disclosure.open = true;
    await disclosure.fire('toggle');
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(allText(disclosure).includes('/fonts/old/hidden'));
    assert.ok(allText(disclosure).includes('<img src=x>'));
    assert.ok(allText(p.document.querySelector('#FontRenameRows')).includes('Renamed'));
});

test('rename statuses and pagination request bounded server pages and clear previous page selections', async () => {
    const p = page();
    p.context.fontManagementState.pageSize = 1;
    p.routes['rename/items'].items = p.routes['rename/items'].items.slice(0, 1);
    await p.context.loadFontRename();
    p.context.fontManagementState.renameFileIds.add('current:0');
    await p.document.querySelector('#RenameItemsNextPage').fire('click');
    assert.equal(p.queries.at(-1).query.page, 2);
    assert.equal(p.queries.at(-1).query.pageSize, 1);
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    await p.document.querySelector('#RenameStatus-failed').fire('click');
    assert.equal(p.queries.at(-1).query.status, 'failed');
    assert.equal(p.queries.at(-1).query.page, 1);
});

test('folder validation locates the backend field', async () => {
    const p = page({ 'POST rename/templates/validate': { errors: [{ field: 'FontFolderTemplate', code: 'unknown-field', offset: 3 }], defaultFileNameTemplate: '{fullName}' } });
    await p.document.querySelector('#AssSubsetterConfigForm').fire('submit');
    assert.equal(p.document.querySelector('#txtFontFolderTemplate').focused, true);
    assert.equal(p.document.querySelector('#txtFontFolderTemplate').selectionStart, 3);
});

test('current rules show whether empty-directory cleanup is enabled', async () => {
    const p = page();
    p.routes['rename/items'].current.rules.removeEmptyDirectories = true;
    await p.context.loadFontRename();
    assert.ok(allText(p.document.querySelector('#RenameRulesValues')).includes('Yes'));
});

test('font writes use Jellyfin authorization accepted by the deployed server', async () => {
    const p = page();
    await p.context.validateNamingRules();
    assert.deepEqual(p.authorizations, ['MediaBrowser Token="test-token"']);
});


test('help uses every server field and only text formatting operations', async () => {
    const p = page();
    await p.context.validateNamingRules();
    for (const locale of ['zh', 'en']) {
        p.context.fontManagementState.locale = locale;
        p.context.renderNamingHelp();
        const help = allText(p.document.querySelector('#FontNamingHelpContent'));
        for (const field of catalog) assert.ok(help.includes('{' + field.name + '}'), field.name);
        assert.ok(help.includes('{upper(family)}') && help.includes('{lower(style)}'));
        for (const removed of ['isCollection', 'resolvedName', 'faces(expression)', 'has(', '?:', '??']) assert.ok(!help.includes(removed), removed);
    }
    assert.equal(p.posts.at(-1).body.formatVersion, 2);
});
test('help enumerates the server catalog even on a validation error', async () => {
    const p = page({ 'POST rename/templates/validate': { errors: [{field:'FontFileNameTemplate',code:'unknown-field',offset:3}], fields: [{name:'originalName',missingBehavior:'skip',isSuffix:false}] } });
    assert.equal(await p.context.validateNamingRules(), false);
    const help = allText(p.document.querySelector('#FontNamingHelpContent'));
    assert.ok(help.includes('{originalName}'));
    assert.ok(!p.document.querySelector('#FontNamingHelpContent').children.some(row => row.children[0].textContent === '{family}'));
});
test('evidence groups values while preserving missing reasons and distinct Faces', () => {
    const p = page();
    const target = new Element();
    const fields = [
        {name:'style',value:null,source:p.hostile,faceIndex:0,nameId:2,missingReason:'invalid-unicode'},
        {name:'style',value:null,source:p.hostile,faceIndex:0,nameId:2,missingReason:'missing-field'},
        {name:'style',value:null,source:p.hostile,faceIndex:1,nameId:2,missingReason:'invalid-unicode'},
        {name:'version',value:'',source:'collection-summary',missingReason:'inconsistent-face-field'},
        {name:'collectionSuffix',value:'',source:'program-suffix',missingReason:null}
    ];
    const original = JSON.stringify(fields);
    p.context.renderNamingEvidence(target, {namingEvidence:{fields:[...fields,...fields],selections:[]}});
    const row = target.children.find(row => row.children[0].textContent === '{style}');
    assert.equal(row.children[1].textContent.split('\n\n').length, 3);
    for (const value of ['Invalid Unicode','Missing field','FaceIndex 1',p.hostile,'Intentionally empty','Inconsistent face field']) assert.ok(allText(target).includes(value), value);
    assert.equal(JSON.stringify(fields), original);
});


test('late old status response cannot overwrite current rows, context or selection', async () => {
    let resolveOld;
    const p = page();
    const current = p.routes['rename/items'];
    p.routes['rename/items'] = query => query.status === 'pending' ? new Promise(resolve => { resolveOld = resolve; }) : { current: null, items: [{ key: 'failed', fileId: 8, canExecute: false, canRollback: false, item: { fileId: 8, oldPath: 'failed.ttf', status: 'FailedIo', error: 'denied' } }], totalCount: 1 };
    const oldRead = p.context.loadFontRename();
    await p.document.querySelector('#RenameStatus-failed').fire('click');
    resolveOld(current);
    await oldRead;
    assert.equal(p.context.fontManagementState.renameStatus, 'failed');
    assert.equal(p.context.fontManagementState.renameRows[0].key, 'failed');
    assert.equal(p.context.fontManagementState.renameContext, null);
    assert.ok(allText(p.document.querySelector('#FontRenameRows')).includes('denied'));
});

test('old syntax focuses original input and prevents save without rewriting on load', async () => {
    const old = '  {resolvedName}';
    const response = { errors: [{ field: 'FontFileNameTemplate', code: 'unknown-field', offset: 3 }] };
    const p = page({ config: { FontFileNameTemplate: old }, 'POST rename/templates/validate': response });
    await p.document.querySelector('#AssSubsetterConfigPage').fire('pageshow');
    const input = p.document.querySelector('#txtFontFileNameTemplate');
    assert.equal(input.value, old);
    assert.equal(input.selectionStart, 3);
    await p.document.querySelector('#AssSubsetterConfigForm').fire('submit');
    assert.equal(p.updates.length, 0);
    response.errors = [];
    await p.document.querySelector('#ResetFontNameTemplateButton').fire('click');
    assert.equal(input.value, defaultTemplate);
    assert.equal(p.posts.at(-1).body.fileNameTemplate, defaultTemplate);
    assert.equal(p.posts.at(-1).body.formatVersion, 2);
    await p.document.querySelector('#CopyDefaultFontNameTemplateButton').fire('click');
    assert.equal(p.document.querySelector('#DefaultFontNameTemplate').value, defaultTemplate);
});

test('saving rules invalidates pending reads and selections before refreshed candidates arrive', async () => {
    const p = page();
    await p.context.loadFontRename();
    p.context.fontManagementState.renameFileIds.add('current:0');
    let resolveOld;
    const result = p.routes['rename/items'];
    p.routes['rename/items'] = () => new Promise(resolve => { resolveOld = resolve; });
    const pending = p.context.loadFontRename();
    await p.document.querySelector('#AssSubsetterConfigPage').fire('pageshow');
    await p.document.querySelector('#AssSubsetterConfigForm').fire('submit');
    resolveOld(result);
    await pending;
    assert.equal(p.context.fontManagementState.renameContext, null);
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    assert.equal(p.document.querySelector('#NormalizeSelectedButton').disabled, true);
});

test('overview distinguishes unchanged and changed scans and explains stale source generations', async () => {
    for (const changed of [false, true]) {
        const p = page({ overview: { publishedGenerationId: 7, latestAnalysis: { id: 9, generationId: 5, status: 'Complete', isCurrent: false, staleReasons: ['generation-superseded', 'directory-policy-changed'] }, lastScan: { generationId: 7, analysisBatchId: 9, changed, changeReasons: changed ? ['files-changed'] : [] } } });
        await p.context.loadFontOverview();
        const text = allText(p.document.querySelector('#FontOverviewValues'));
        assert.ok(text.includes(changed ? 'new generation and analysis published' : 'unchanged, generation and analysis reused'));
        assert.ok(text.includes('Source generation superseded'));
        assert.ok(text.includes('Directory policy changed'));
    }
});


test('overview uses one API read to render inventory, database health and performance', async () => {
    const p = page({ overview: {
        publishedGenerationId: 31,
        databaseHealth: { schemaVersion: 7, journalMode: 'wal', size: 4096, publishedGenerationId: 31, recoveredCorruptDatabasePath: '<backup>', checkedUtc: '2026-10-07T00:00:00Z' },
        phaseTimings: [{ phase: 'inventory-scan', duration: '00:00:01', itemCount: 12, reusedCount: 9, recordedUtc: '2026-10-07T00:00:00Z' }]
    } });
    await p.context.loadActiveFontView();
    const text = allText(p.document.querySelector('#FontDatabaseValues'));
    assert.deepEqual(p.gets, ['overview']);
    assert.ok(text.includes('wal'));
    assert.ok(text.includes('<backup>'));
    assert.ok(allText(p.document.querySelector('#FontOverviewValues')).includes('31'));
    assert.equal(p.document.querySelector('#FontPerformanceRows').children.length, 1);
    assert.ok(allText(p.document.querySelector('#FontPerformanceRows')).includes('12'));
    assert.ok(allText(p.document.querySelector('#FontPerformanceRows')).includes('9'));
    const inventoryLabels = descendants(p.document.querySelector('#FontOverviewValues')).filter(element => element.tagName === 'dt').map(element => element.textContent);
    const healthLabels = descendants(p.document.querySelector('#FontDatabaseValues')).filter(element => element.tagName === 'dt').map(element => element.textContent);
    assert.ok(inventoryLabels.every(label => !healthLabels.includes(label)), 'Each metric appears in only one overview section');
    assert.equal(p.posts.length, 0);
});

test('rename cards select from header and paths, highlight selection, and preserve it on rerender without checkboxes', async () => {
    const p = page();
    await p.context.loadFontRename();
    let card = p.document.querySelector('#FontRenameRows').children[0];
    assert.equal(descendants(card).filter(element => element.type === 'checkbox').length, 0);
    assert.equal(card.getAttribute('role'), 'button');
    assert.equal(card.getAttribute('aria-pressed'), 'false');
    assert.equal(card.tabIndex, 0);
    card.children[0].children[0].fire('click');
    assert.ok(p.context.fontManagementState.renameFileIds.has('current:0'));
    assert.equal(card.getAttribute('aria-pressed'), 'true');
    assert.ok(card.className.includes('fontRenameCard--selected'));
    p.context.renderRenameItems(p.context.fontManagementState.renameRows);
    card = p.document.querySelector('#FontRenameRows').children[0];
    assert.equal(card.getAttribute('aria-pressed'), 'true');
    card.children[1].children[0].fire('click');
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    assert.equal(card.getAttribute('aria-pressed'), 'false');
    assert.ok(!card.className.includes('fontRenameCard--selected'));
    card.fire('keydown', { key: 'Enter' });
    assert.ok(p.context.fontManagementState.renameFileIds.has('current:0'));
    card.fire('keydown', { key: ' ' });
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
});

test('naming disclosure summary does not select the card or intercept its keyboard toggle', async () => {
    const p = page();
    await p.context.loadFontRename();
    const card = p.document.querySelector('#FontRenameRows').children[0];
    const summary = descendants(card).find(element => element.tagName === 'summary');
    assert.ok(summary);
    summary.fire('click');
    let prevented = false;
    summary.fire('keydown', { key: 'Enter', preventDefault() { prevented = true; } });
    summary.fire('keydown', { key: ' ', preventDefault() { prevented = true; } });
    assert.equal(prevented, false, 'Disclosure keeps its native keyboard behavior');
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    card.fire('click');
    summary.fire('click');
    assert.ok(p.context.fontManagementState.renameFileIds.has('current:0'));
});

test('whole-card selection rejects skipped, expired and busy rows while allowing rollback history', async () => {
    const p = page();
    await p.context.loadFontRename();
    const skipped = p.document.querySelector('#FontRenameRows').children[1];
    assert.equal(skipped.getAttribute('aria-disabled'), 'true');
    skipped.fire('click');
    skipped.fire('keydown', { key: 'Enter' });
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    p.context.fontManagementState.renameCurrent = false;
    p.context.renderRenameItems(p.context.fontManagementState.renameRows);
    p.document.querySelector('#FontRenameRows').children[0].fire('click');
    assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    const row = { ...p.context.fontManagementState.renameRows[0], canExecute: false, canRollback: true };
    p.context.renderRenameItems([row]);
    p.document.querySelector('#FontRenameRows').children[0].fire('click');
    assert.ok(p.context.fontManagementState.renameFileIds.has(row.key));
    p.context.fontManagementState.renameFileIds.clear();
    for (const status of ['pending', 'running']) {
        p.context.fontManagementState.renameCandidateState = { status };
        p.context.renderRenameItems([row]);
        const busy = p.document.querySelector('#FontRenameRows').children[0];
        busy.fire('click');
        busy.fire('keydown', { key: 'Enter' });
        assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
    }
});

test('naming evidence omits template version usage and labels every generation value in both languages', async () => {
    for (const locale of ['zh', 'en']) {
        const p = page();
        p.context.fontManagementState.locale = locale;
        await p.context.loadFontRename();
        const rules = p.document.querySelector('#RenameRulesValues');
        const card = p.document.querySelector('#FontRenameRows').children[0];
        const labels = [...descendants(rules), ...descendants(card)].filter(element => element.className === 'fontDetailLabel');
        assert.ok(labels.every(element => typeof element.textContent === 'string' && element.textContent.length > 0));
        const text = allText(card);
        assert.ok(text.toLowerCase().includes(locale === 'zh' ? '发布代次' : 'published generation'));
        assert.ok(!text.includes('文件名模板使用版本') && !text.includes('Filename template uses version'));
        assert.ok(text.includes('2.003'), 'The reliable font version remains available');
    }
});

test('skipped unclassifiable fonts show saved inspection causes as localized text', async () => {
    const causes = [
        ['Only legacy cmap encoding records are present; no Unicode mapping can be proven.', '只有旧式 cmap 编码记录'],
        ['The cmap format 4 glyph array reference lies outside the declared subtable length.', '字形引用超出子表声明长度'],
        ['Unicode cmap mappings are invalid or inconsistent across subtables.', 'Unicode cmap 映射无效']
    ];
    for (const [inspectionError, expected] of causes) {
        const p = page();
        p.context.fontManagementState.locale = 'zh';
        const row = p.routes['rename/items'].items[1];
        row.item.error = 'unclassifiable-or-link';
        row.item.inspectionError = inspectionError;
        await p.context.loadFontRename();
        const text = allText(p.document.querySelector('#FontRenameRows').children[1]);
        assert.ok(text.includes('跳过原因'));
        assert.ok(text.includes(expected));
        assert.ok(!text.includes('存在链接'), 'A failed inspection must not imply a link was found');
    }
    const p = page();
    const row = p.routes['rename/items'].items[1];
    row.item.error = 'unclassifiable-or-link';
    row.item.inspectionError = p.hostile;
    await p.context.loadFontRename();
    assert.ok(allText(p.document.querySelector('#FontRenameRows').children[1]).includes(p.hostile));
});
const pagedViews = [
    { prefix: 'Report', route: 'reports', load: 'loadAnalysisReports', statePage: 'reportPage' },
    { prefix: 'Quarantine', route: 'quarantine/items', load: 'loadFontQuarantine', statePage: 'quarantinePage' },
    { prefix: 'RenameItems', route: 'rename/items', load: 'loadFontRename', statePage: 'renameItemPage' }
];

for (const view of pagedViews) {
    test(view.prefix + ' page dropdown jumps directly using bounded page size and preserves filters', async () => {
        const p = page();
        const initial = view.route === 'rename/items' ? p.routes[view.route] : { items: [] };
        p.routes[view.route] = query => ({ ...initial, page: query.page, pageSize: query.pageSize, totalCount: 401 });
        p.context.fontManagementState.pageSize = 999;
        p.context.fontManagementState.analysisBatchId = 7;
        p.context.fontManagementState.quarantineStatus = 'failed';
        p.context.fontManagementState.renameStatus = 'skipped';
        const filters = { '#ReportRelationshipFilter': 'strictFaceDuplicate', '#ReportUnclassifiableFilter': 'exclude', '#ReportDirectoryFilter': '/custom', '#ReportTechnologyFilter': 'TrueType', '#ReportActionFilter': 'quarantineCandidate' };
        for (const [selector, value] of Object.entries(filters)) p.document.querySelector(selector).value = value;
        await p.context[view.load]();
        const select = p.document.querySelector('#' + view.prefix + 'PageSelect');
        assert.deepEqual(select.options.map(option => option.value), ['1', '2', '3']);
        assert.deepEqual(select.options.map(option => option.textContent), ['Page 1', 'Page 2', 'Page 3']);
        assert.equal(select.value, '1');
        assert.equal(select.disabled, false);
        p.context.fontManagementState.renameFileIds.add('current:0');
        select.value = '3';
        await select.fire('change');
        const request = p.queries.at(-1);
        assert.equal(request.route, view.route);
        assert.equal(request.query.page, 3);
        assert.equal(request.query.pageSize, 200);
        if (view.prefix === 'Report') {
            assert.equal(request.query.analysisBatchId, 7);
            assert.equal(request.query.directory, '/custom');
            assert.equal(request.query.relationship, 'strictFaceDuplicate');
            assert.equal(request.query.unclassifiable, 'exclude');
            assert.equal(request.query.technology, 'TrueType');
            assert.equal(request.query.action, 'quarantineCandidate');
        } else {
            assert.equal(request.query.status, view.prefix === 'Quarantine' ? 'failed' : 'skipped');
        }
        assert.equal(select.value, '3');
        assert.equal(p.document.querySelector('#' + view.prefix + 'PreviousPage').disabled, false);
        assert.equal(p.document.querySelector('#' + view.prefix + 'NextPage').disabled, true);
        if (view.prefix === 'RenameItems') assert.equal(p.context.fontManagementState.renameFileIds.size, 0);
        assert.equal(p.posts.length, 0);
    });
}

test('page dropdowns disable empty or single pages and reject invalid or redundant jumps', async () => {
    for (const view of pagedViews) {
        const p = page();
        const initial = view.route === 'rename/items' ? p.routes[view.route] : { items: [] };
        let totalCount = 0;
        p.routes[view.route] = query => ({ ...initial, page: query.page, totalCount });
        p.context.fontManagementState.analysisBatchId = 7;
        p.context.fontManagementState.locale = 'zh';
        const select = p.document.querySelector('#' + view.prefix + 'PageSelect');
        for (const total of [0, 1, 50]) {
            totalCount = total;
            await p.context[view.load]();
            assert.deepEqual(select.options.map(option => option.value), ['1']);
            assert.equal(select.options[0].textContent, '第 1 页');
            assert.equal(select.value, '1');
            assert.equal(select.disabled, true);
        }
        totalCount = 101;
        await p.context[view.load]();
        const reads = p.gets.length;
        for (const value of ['', '0', '-1', '4', '1.5', 'NaN', '1']) {
            select.value = value;
            await select.fire('change');
            assert.equal(p.gets.length, reads);
            assert.equal(p.context.fontManagementState[view.statePage], 1);
        }
    }
});

test('page dropdowns follow previous and next buttons and reset after filter or status changes', async () => {
    for (const view of pagedViews) {
        const p = page();
        const initial = view.route === 'rename/items' ? p.routes[view.route] : { items: [] };
        p.routes[view.route] = query => ({ ...initial, page: query.page, totalCount: 101 });
        p.context.fontManagementState.analysisBatchId = 7;
        await p.context[view.load]();
        const select = p.document.querySelector('#' + view.prefix + 'PageSelect');
        await p.document.querySelector('#' + view.prefix + 'NextPage').fire('click');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(select.value, '2');
        await p.document.querySelector('#' + view.prefix + 'PreviousPage').fire('click');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(select.value, '1');
        select.value = '3';
        await select.fire('change');
        const resetSelector = view.prefix === 'Report' ? '#ReportApplyFilters'
            : view.prefix === 'Quarantine' ? '#QuarantineFailedTab' : '#RenameStatus-skipped';
        await p.document.querySelector(resetSelector).fire('click');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(select.value, '1');
        assert.equal(p.queries.at(-1).query.page, 1);
    }
});

test('published markup has labeled jump controls and selection highlights outside the card', async () => {
    const html = await readFile(new URL('../../Jellyfin.Plugin.AssSubsetter/Configuration/configPage.html', import.meta.url), 'utf8');
    for (const view of pagedViews) {
        assert.ok(html.includes('id="' + view.prefix + 'PageSelect"'));
        assert.match(html, new RegExp('<label[^>]*>\\s*<span data-i18n="jumpToPage">[^<]+</span>\\s*<select id="' + view.prefix + 'PageSelect"'));
    }
    const selectedStyle = html.match(/\.fontRenameCard--selected\s*\{([^}]+)\}/)?.[1];
    assert.ok(selectedStyle);
    assert.match(selectedStyle, /outline:\s*2px solid/);
    assert.match(selectedStyle, /outline-offset:\s*2px/);
    assert.doesNotMatch(selectedStyle, /background|inset|border-color/);
});
test('rapid report page jumps keep the latest page when earlier reads complete or fail later', async () => {
    for (const olderFails of [false, true]) {
        const p = page({ reports: query => ({ items: [], page: query.page, totalCount: 101 }) });
        p.context.fontManagementState.analysisBatchId = 7;
        await p.context.loadAnalysisReports();
        let resolveOld;
        let rejectOld;
        p.routes.reports = query => query.page === 2 ? new Promise((resolve, reject) => { resolveOld = resolve; rejectOld = reject; })
            : { items: [], page: query.page, totalCount: 101 };
        const select = p.document.querySelector('#ReportPageSelect');
        select.value = '2';
        const oldRead = select.fire('change');
        select.value = '3';
        await select.fire('change');
        if (olderFails) rejectOld(new Error('outdated request failed'));
        else resolveOld({ items: [], page: 2, totalCount: 101 });
        await oldRead;
        assert.equal(select.value, '3');
        assert.equal(p.context.fontManagementState.reportPage, 3);
        assert.ok(!allText(p.document.querySelector('#FontReportState')).includes('outdated request failed'));
    }
});
test('database diagnostics and maintenance action belong to the single overview tab', async () => {
    const html = await readFile(new URL('../../Jellyfin.Plugin.AssSubsetter/Configuration/configPage.html', import.meta.url), 'utf8');
    const overview = html.match(/<section id="FontOverviewView"[\s\S]+?<\/section>/)?.[0];
    assert.ok(overview);
    for (const id of ['FontDatabaseHeading', 'FontDatabaseValues', 'FontPerformanceRows', 'RebuildDerivedDataButton']) {
        assert.ok(overview.includes('id="' + id + '"'));
    }
    assert.ok(!html.includes('id="FontDatabaseTab"'));
    assert.ok(!html.includes('data-font-view="database"'));
    assert.ok(!html.includes('id="FontDatabaseState"'), 'A single read has a single loading/error state');
});

test('overview explains scan change reasons and analysis validity instead of generic status cards', async () => {
    for (const locale of ['zh', 'en']) {
        for (const changed of [false, true]) {
            const p = page({ overview: {
                publishedGenerationId: 7,
                latestAnalysis: { id: 9, generationId: changed ? 5 : 7, isCurrent: !changed, staleReasons: changed ? ['generation-superseded'] : [] },
                lastScan: { generationId: 7, analysisBatchId: 9, changed, changeReasons: changed ? ['files-changed'] : [] },
                databaseHealth: { schemaVersion: 7, journalMode: 'wal', size: 4096 }
            } });
            p.context.fontManagementState.locale = locale;
            await p.context.loadFontOverview();
            const list = p.document.querySelector('#FontOverviewValues');
            const labels = descendants(list).filter(element => element.tagName === 'dt').map(element => element.textContent);
            const text = allText(list);
            assert.ok(!labels.includes(locale === 'zh' ? '状态' : 'Status'));
            assert.ok(labels.includes(locale === 'zh' ? '分析结论有效性' : 'Analysis Validity'));
            assert.equal(labels.includes(locale === 'zh' ? '上次扫描变更原因' : 'Last Scan Change Reasons'), changed);
            assert.ok(text.includes(locale === 'zh' ? changed ? '分析已过期' : '分析有效' : changed ? 'Analysis is stale' : 'Analysis is current'));
            assert.ok(!text.includes('预览') && !text.includes('preview') && !text.includes('Preview'));
            assert.ok(!text.includes(' ·  · '), 'Empty reason lists must not leave empty segments');
        }
    }
});

test('overview without analysis still shows database diagnostics and successful page state', async () => {
    const p = page({ overview: {
        publishedFileCount: 0, publishedFaceCount: 0, latestAnalysis: null,
        databaseHealth: { schemaVersion: 7, journalMode: 'wal', size: 4096 },
        phaseTimings: []
    } });
    await p.context.loadFontOverview();
    assert.equal(p.document.querySelector('#FontOverviewState').className, 'fontManagementState fontManagementState-ready');
    assert.equal(p.document.querySelector('#ExportAnalysisButton').disabled, true);
    assert.ok(allText(p.document.querySelector('#FontDatabaseValues')).includes('wal'));
    assert.equal(p.document.querySelector('#FontPerformanceRows').children.length, 1);
    assert.ok(allText(p.document.querySelector('#FontPerformanceRows')).includes('No records to display.'));
});

test('merged overview reports a failed read without extra requests or maintenance calls', async () => {
    const p = page({ overview: () => { throw new Error('overview unavailable'); } });
    await p.context.loadActiveFontView();
    assert.deepEqual(p.gets, ['overview']);
    assert.equal(p.posts.length, 0);
    assert.equal(p.document.querySelector('#FontOverviewState').className, 'fontManagementState fontManagementState-error');
    assert.ok(allText(p.document.querySelector('#FontOverviewState')).includes('overview unavailable'));
});

test('rebuild derived data refreshes all overview sections from the combined response', async () => {
    let reads = 0;
    const p = page({ overview: () => ({
        publishedGenerationId: ++reads,
        databaseHealth: { schemaVersion: reads, journalMode: 'wal', size: reads * 1024 },
        phaseTimings: [{ phase: 'inventory-scan', duration: '00:00:01', itemCount: reads, reusedCount: 0 }]
    }) });
    p.context.window.confirm = () => true;
    await p.context.loadFontOverview();
    await p.document.querySelector('#RebuildDerivedDataButton').fire('click');
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(p.gets, ['overview', 'overview']);
    assert.equal(p.posts.length, 1);
    assert.equal(p.posts[0].path, 'operations/rebuild-derived-data');
    assert.ok(allText(p.document.querySelector('#FontDatabaseValues')).includes('2.0 KiB'));
    assert.ok(allText(p.document.querySelector('#FontOverviewValues')).includes('2'));
    assert.ok(allText(p.document.querySelector('#FontPerformanceRows')).includes('2'));
});
