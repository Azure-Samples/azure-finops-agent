import assert from 'node:assert/strict';
import test from 'node:test';
import { isNumericCell, renderMarkdown } from '../../src/Dashboard/frontend/src/markdown.js';

test('bold works without surrounding spaces and identifiers stay byte-for-byte', () => {
  assert.equal(renderMarkdown('**Total:**USD 12.34'), '<p><strong>Total:</strong>USD 12.34</p>');
  assert.equal(renderMarkdown('***Key***'), '<p><strong><em>Key</em></strong></p>');
  assert.equal(renderMarkdown('Use Standard_D4s_v5 in eastus2'), '<p>Use Standard_D4s_v5 in eastus2</p>');
  assert.equal(renderMarkdown('snake_case_name and __init__'), '<p>snake_case_name and __init__</p>');
  assert.equal(renderMarkdown('5 * 3 * 2 = 30'), '<p>5 * 3 * 2 = 30</p>');
  assert.equal(renderMarkdown('Microsoft.Compute/* and *.json'), '<p>Microsoft.Compute/* and *.json</p>');
  assert.equal(renderMarkdown('an *italic* and _also_ word'), '<p>an <em>italic</em> and <em>also</em> word</p>');
  assert.equal(renderMarkdown('~~old~~ new'), '<p><del>old</del> new</p>');
  assert.equal(renderMarkdown('\\*not italic\\*'), '<p>*not italic*</p>');
});

test('paragraphs, line breaks, headings and rules', () => {
  assert.equal(renderMarkdown('one\ntwo\n\nthree'), '<p>one<br/>two</p><p>three</p>');
  assert.equal(renderMarkdown('# Title\n## Section\n### Sub\n#### Deep'), '<h2>Title</h2><h3>Section</h3><h4>Sub</h4><h4>Deep</h4>');
  assert.equal(renderMarkdown('above\n\n---\n\nbelow'), '<p>above</p><hr/><p>below</p>');
  assert.equal(renderMarkdown('#hashtag'), '<p>#hashtag</p>');
});

test('nested and ordered lists, including a list right after a paragraph', () => {
  assert.equal(
    renderMarkdown('Top savings:\n- **VMs**: resize\n  - Standard_D8s_v5 to D4s\n- Disks'),
    '<p>Top savings:</p><ul><li><strong>VMs</strong>: resize<ul><li>Standard_D8s_v5 to D4s</li></ul></li><li>Disks</li></ul>',
  );
  assert.equal(renderMarkdown('3. third\n4. fourth'), '<ol start="3"><li>third</li><li>fourth</li></ol>');
  assert.equal(
    renderMarkdown('1. First\n\n2. Second\n   - detail'),
    '<ol><li>First</li><li>Second<ul><li>detail</li></ul></li></ol>',
  );
  assert.equal(renderMarkdown('- a\n\nAfter'), '<ul><li>a</li></ul><p>After</p>');
  assert.match(renderMarkdown('- [x] done\n- [ ] todo'), /md-task md-task--done.*md-task"/);
});

test('tables align numbers, keep inline formatting and prompt chips', () => {
  const html = renderMarkdown(
    '| Service | Cost | Status |\n|:--|--:|:-:|\n| **VMs** | USD 1,200 | OK |\n| `Disks` | USD 300 | [Ask](prompt:Why disks?) |',
  );
  assert.match(html, /^<div class="wt-wrap"><table class="wow-table">/);
  assert.match(html, /<th class="wt-num wt-r">Cost<\/th>/);
  assert.match(html, /<td><strong>VMs<\/strong><\/td>/);
  assert.match(html, /<td class="wt-num wt-r">USD 1,200<\/td>/);
  assert.match(html, /<span class="wt-tag wt-tag-g">OK<\/span>/);
  assert.match(html, /<code class="wt-id">Disks<\/code>/);
  assert.match(html, /<button type="button" class="prompt-chip" data-prompt="Why disks\?">Ask<\/button>/);
});

test('labels that contain digits stay text while figures align right', () => {
  const html = renderMarkdown(
    [
      '| Model | Quality | Latency | Global USD/1M in / cached / out |',
      '|---|--:|--:|---|',
      '| GPT-6 Astra | 53 | 341.88s | 10 / 1 / 50 |',
      '| `6-luna` | 38 | 128.78s | 0.1 / 0.01 / 0.5 |',
      '| Grok 4.6 | — | N/A | 2 / 0.5 / 10 |',
      '| DeepSeek V4 Pro | 36 | 1.75s | USD 1.2 |',
    ].join('\n'),
  );
  assert.match(html, /<th>Model<\/th>/);
  assert.match(html, /<td>GPT-6 Astra<\/td>/);
  assert.match(html, /<td><code class="wt-id">6-luna<\/code><\/td>/);
  assert.match(html, /<th class="wt-num wt-r">Quality<\/th>/);
  assert.match(html, /<td class="wt-num wt-r">341.88s<\/td>/);
  assert.match(html, /<td class="wt-num">10 \/ 1 \/ 50<\/td>/);
});

test('numeric cells need a leading figure and few unit words', () => {
  for (const cell of ['USD 1,200', '$300', '3.2s', '10 / 1 / 50', '▲ 12%', '-5%', '2 vCPU / 8 GiB', '15k', '1.2M', '2026-09-01', '**52**', '46²'])
    assert.equal(isNumericCell(cell), true, cell);
  for (const cell of ['GPT-6 Astra', '6-astra', '4o-mini', 'Standard_D4s_v5', 'Short: 10 / 1 / 50', 'Not verified²', 'GPT 6', 'OK', '', 'USD'])
    assert.equal(isNumericCell(cell), false, cell);
});

test('code blocks and spans are literal', () => {
  assert.equal(
    renderMarkdown('```powershell\nGet-AzVM | Where { $_.Name -like "*web*" }\n```'),
    '<pre><code class="lang-powershell">Get-AzVM | Where { $_.Name -like "*web*" }</code></pre>',
  );
  assert.equal(renderMarkdown('`**not bold**` and `a_b_c`'), '<p><code>**not bold**</code> and <code>a_b_c</code></p>');
  assert.equal(renderMarkdown('```\nunterminated'), '<pre><code>unterminated</code></pre>');
});

test('links open safely and only for http(s)', () => {
  assert.equal(
    renderMarkdown('[Pricing](https://prices.azure.com/api/retail/prices?$filter=a&b=c)'),
    '<p><a href="https://prices.azure.com/api/retail/prices?$filter=a&amp;b=c" target="_blank" rel="noopener noreferrer">Pricing</a></p>',
  );
  assert.equal(
    renderMarkdown('See https://learn.microsoft.com/azure/cost-management-billing/ (docs).'),
    '<p>See <a href="https://learn.microsoft.com/azure/cost-management-billing/" target="_blank" rel="noopener noreferrer">https://learn.microsoft.com/azure/cost-management-billing/</a> (docs).</p>',
  );
  assert.equal(renderMarkdown('[x](javascript:alert(1))'), '<p>[x](javascript:alert(1))</p>');
  assert.equal(
    renderMarkdown('[x](https://a.test/"onmouseover="alert(1))'),
    '<p><a href="https://a.test/&quot;onmouseover=&quot;alert(1)" target="_blank" rel="noopener noreferrer">x</a></p>',
  );
});

test('markup is escaped everywhere and entities are preserved', () => {
  assert.equal(renderMarkdown('<img src=x onerror=alert(1)>'), '<p>&lt;img src=x onerror=alert(1)&gt;</p>');
  assert.equal(renderMarkdown('| a |\n|---|\n| <script>x</script> |').includes('<script>'), false);
  assert.equal(renderMarkdown('- <b>x</b>'), '<ul><li>&lt;b&gt;x&lt;/b&gt;</li></ul>');
  assert.equal(renderMarkdown('[<b>](prompt:"><img>)'), '<div class="prompt-chip-row"><button type="button" class="prompt-chip" data-prompt="&quot;&gt;&lt;img&gt;">&lt;b&gt;</button></div>');
  assert.equal(renderMarkdown('Ask [<b>](prompt:"><img>)'), '<p>Ask <button type="button" class="prompt-chip" data-prompt="&quot;&gt;&lt;img&gt;">&lt;b&gt;</button></p>');
  assert.equal(renderMarkdown('AT&T &amp; &#65;'), '<p>AT&amp;T &amp; &#65;</p>');
  assert.equal(renderMarkdown('> quoted **note**\n> more'), '<blockquote><p>quoted <strong>note</strong><br/>more</p></blockquote>');
});

test('citation markers and empty input', () => {
  assert.equal(renderMarkdown('Price \uE200cite\uE202turn0search0\uE201 now'), '<p>Price now</p>');
  assert.equal(renderMarkdown(''), '');
  assert.equal(renderMarkdown(null), '');
});

test('next-step chips that end an answer become one row of buttons', () => {
  const chip = (label, prompt) => `<button type="button" class="prompt-chip" data-prompt="${prompt}">${label}</button>`;
  const row = `<div class="prompt-chip-row">${chip('Top resources', 'Show the top resources')}${chip('Script', 'Write the cleanup script')}</div>`;
  assert.equal(
    renderMarkdown('Spend rose 12%.\n\n[Top resources](prompt:Show the top resources) · [Script](prompt:Write the cleanup script)'),
    `<p>Spend rose 12%.</p>${row}`,
  );
  assert.equal(
    renderMarkdown('Spend rose 12%.\n\n- [Top resources](prompt:Show the top resources)\n- [Script](prompt:Write the cleanup script)'),
    `<p>Spend rose 12%.</p>${row}`,
  );
  // Chips inside a sentence or table, or followed by more text, stay where they are.
  assert.equal(
    renderMarkdown('Next: [Script](prompt:Write the cleanup script)'),
    `<p>Next: ${chip('Script', 'Write the cleanup script')}</p>`,
  );
  assert.equal(
    renderMarkdown('[Script](prompt:Write the cleanup script)\n\nDone.'),
    `<p>${chip('Script', 'Write the cleanup script')}</p><p>Done.</p>`,
  );
  assert.doesNotMatch(renderMarkdown('| a | b |\n|---|---|\n| x | [Ask](prompt:Why?) |'), /prompt-chip-row/);
});
