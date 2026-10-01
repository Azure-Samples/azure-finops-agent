import assert from 'node:assert/strict';
import test from 'node:test';
import { renderMarkdown } from '../../src/Dashboard/frontend/src/markdown.js';

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
  assert.match(html, /<code>Disks<\/code>/);
  assert.match(html, /<button type="button" class="prompt-chip" data-prompt="Why disks\?">Ask<\/button>/);
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
  assert.equal(renderMarkdown('[<b>](prompt:"><img>)'), '<p><button type="button" class="prompt-chip" data-prompt="&quot;&gt;&lt;img&gt;">&lt;b&gt;</button></p>');
  assert.equal(renderMarkdown('AT&T &amp; &#65;'), '<p>AT&amp;T &amp; &#65;</p>');
  assert.equal(renderMarkdown('> quoted **note**\n> more'), '<blockquote><p>quoted <strong>note</strong><br/>more</p></blockquote>');
});

test('citation markers and empty input', () => {
  assert.equal(renderMarkdown('Price \uE200cite\uE202turn0search0\uE201 now'), '<p>Price now</p>');
  assert.equal(renderMarkdown(''), '');
  assert.equal(renderMarkdown(null), '');
});
