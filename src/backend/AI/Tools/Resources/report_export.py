import base64
import csv
import html
import io
import json
import math
import sys


def safe_cell(value):
    if value is not None and not isinstance(value, (str, int, float, bool)):
        raise ValueError("Cells must be scalar values")
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("Numeric cells must be finite")
    if isinstance(value, str) and value.lstrip().startswith(("=", "+", "-", "@", "\t", "\r", "\n")):
        return "'" + value
    return value


THEME = """
:root {color-scheme:light;--cp-bg:#f7f4ef;--cp-bg-elevated:#fcfbf8;--cp-surface:#ffffff;--cp-surface-soft:#f5f5f5;--cp-border:#dedede;--cp-border-strong:#919191;--cp-text:#242424;--cp-text-muted:#5c5c5c;--cp-text-soft:#6f6f6f;--cp-accent:#b11f4b;--cp-accent-hover:#9a1a41;--cp-accent-soft:rgba(177,31,75,.08);--cp-accent-fg:#ffffff;--cp-success:#16a34a;--cp-danger:#dc2626;--cp-warning:#f59e0b;--cp-link:#0078d4;--cp-shadow:0 18px 48px rgba(0,0,0,.12);--cp-overlay:rgba(255,255,255,.8);--cp-panel:rgba(255,255,255,.86);--cp-panel-strong:rgba(255,255,255,.96);--cp-sheen:rgba(255,255,255,.55);--cp-highlight:rgba(177,31,75,.12)}
html[data-theme="dark"] {color-scheme:dark;--cp-bg:#3d3b3a;--cp-bg-elevated:#343231;--cp-surface:#292929;--cp-surface-soft:#2e2e2e;--cp-border:#474747;--cp-border-strong:#5f5f5f;--cp-text:#dedede;--cp-text-muted:#919191;--cp-text-soft:#b0b0b0;--cp-accent:#fd8ea1;--cp-accent-hover:#fb7b91;--cp-accent-soft:rgba(253,142,161,.14);--cp-accent-fg:#1a1a1a;--cp-success:#4ade80;--cp-danger:#f87171;--cp-warning:#fbbf24;--cp-link:#4da6ff;--cp-shadow:0 18px 48px rgba(0,0,0,.32);--cp-overlay:rgba(41,41,41,.88);--cp-panel:rgba(41,41,41,.72);--cp-panel-strong:rgba(41,41,41,.96);--cp-sheen:rgba(255,255,255,.04);--cp-highlight:rgba(253,142,161,.12)}
"""

CHART_TYPES = ("bar", "horizontal_bar", "line", "pie")
PALETTE = ("#0f6cbd", "#c239b3", "#13a10e", "#ca5010", "#8764b8", "#038387", "#986f0b", "#d13438", "#5c2e91", "#e3008c", "#498205", "#4f6bed")
MAX_TEXT = 4000


def check_text_and_chart(where, sheet):
    """Validate a sheet's optional text and its chart, which is drawn from the sheet's own rows."""
    text = sheet.get("text")
    if text is not None and (not isinstance(text, str) or len(text) > MAX_TEXT):
        raise ValueError(f"{where}: text must be a string of at most {MAX_TEXT} characters")
    chart = sheet.get("chart")
    if chart is None:
        return
    if not isinstance(chart, dict):
        raise ValueError(f"{where}: chart must be an object with type, label and values")
    kind = chart.get("type")
    if kind not in CHART_TYPES:
        raise ValueError(f"{where}: chart type must be bar, horizontal_bar, line or pie")
    columns, rows = sheet["columns"], sheet["rows"]
    if not isinstance(chart.get("label"), str) or chart["label"] not in columns:
        raise ValueError(f"{where}: chart label must name one of the sheet's columns")
    values = chart.get("values")
    most = 1 if kind == "pie" else 4
    if not isinstance(values, list) or not 1 <= len(values) <= most or not all(isinstance(value, str) for value in values) \
            or len(set(values)) != len(values) or any(value not in columns or value == chart["label"] for value in values):
        raise ValueError(f"{where}: chart values must name 1 to {most} other columns of the sheet")
    title = chart.get("title")
    if title is not None and (not isinstance(title, str) or len(title) > 200):
        raise ValueError(f"{where}: chart title must be a string of at most 200 characters")
    limit = 12 if kind == "pie" else 100
    if not 1 <= len(rows) <= limit:
        raise ValueError(f"{where}: a {kind} chart draws 1 to {limit} rows; chart a smaller or aggregated sheet")
    numbers = []
    for index, row in enumerate(rows, 1):
        for value in values:
            cell = row[columns.index(value)]
            if cell is None and kind != "pie":
                continue
            if isinstance(cell, bool) or not isinstance(cell, (int, float)):
                raise ValueError(f"{where}: row {index}: chart values must be numbers")
            if kind == "pie" and cell < 0:
                raise ValueError(f"{where}: row {index}: pie chart values cannot be negative")
            numbers.append(cell)
    if not numbers or (kind == "pie" and sum(numbers) <= 0):
        raise ValueError(f"{where}: chart values must include a number above zero")


def number_text(value):
    magnitude = abs(value)
    for threshold, divisor, suffix in ((1e9, 1e9, "B"), (1e6, 1e6, "M"), (1e4, 1e3, "K")):
        if magnitude >= threshold:
            return f"{value / divisor:,.1f}".rstrip("0").rstrip(".") + suffix
    text = f"{value:,.2f}".rstrip("0").rstrip(".")
    return "0" if text in ("", "-0") else text


def exact_text(value):
    text = f"{value:,.2f}".rstrip("0").rstrip(".")
    return "0" if text in ("", "-0") else text


def nice_ticks(low, high, count=5):
    if high <= low:
        high = low + 1
    raw = (high - low) / count
    power = 10 ** math.floor(math.log10(raw))
    step = next(multiple * power for multiple in (1, 2, 2.5, 5, 10) if multiple * power >= raw)
    start, end = math.floor(low / step) * step, math.ceil(high / step) * step
    return [round(start + index * step, 10) for index in range(int(round((end - start) / step)) + 1)]


def tick_texts(ticks):
    """One scale per axis, so 8,000 is never followed by 10K."""
    largest = max(abs(tick) for tick in ticks)
    for threshold, divisor, suffix in ((1e9, 1e9, "B"), (1e6, 1e6, "M"), (1e4, 1e3, "K")):
        if largest >= threshold:
            return [("0" if tick == 0 else f"{tick / divisor:,.1f}".rstrip("0").rstrip(".") + suffix) for tick in ticks]
    return [number_text(tick) for tick in ticks]


def chart_svg(sheet, number):
    """Render a sheet's chart as static, escaped SVG: no script, link or external resource."""
    escape = lambda value: html.escape(str(value), quote=True)
    short = lambda text, size=24: text if len(text) <= size else text[: size - 1] + "\u2026"
    chart, columns, rows = sheet["chart"], sheet["columns"], sheet["rows"]
    kind = chart["type"]
    label_at = columns.index(chart["label"])
    series = [(name, columns.index(name)) for name in chart["values"]]
    labels = ["" if row[label_at] is None else str(row[label_at]) for row in rows]
    title = chart.get("title") or sheet.get("name") or "Chart"
    width, top, parts = 960, 56, []
    title_id = f"chart-{number}-title"
    parts.append(f'<text x="16" y="28" font-size="16" font-weight="600">{escape(title)}</text>')
    if kind != "pie" and len(series) > 1:
        x = 16
        for position, (name, _) in enumerate(series):
            parts.append(f'<rect x="{x}" y="38" width="10" height="10" fill="{PALETTE[position % len(PALETTE)]}"/>'
                         f'<text x="{x + 14}" y="47">{escape(short(name))}</text>')
            x += 28 + 7 * len(short(name))
    if kind == "pie":
        values = [float(row[series[0][1]]) for row in rows]
        total = sum(values)
        legend = tick_texts(values)
        cx, cy, radius = 190, top + 160, 150
        height = max(top + 340, top + 24 * len(rows) + 24)
        angle = -math.pi / 2
        for position, (label, value) in enumerate(zip(labels, values)):
            color = PALETTE[position % len(PALETTE)]
            share = value / total
            tip = f"<title>{escape(label)}: {escape(exact_text(value))} ({share:.1%})</title>"
            if share >= 0.999999:
                parts.append(f'<circle cx="{cx}" cy="{cy}" r="{radius}" fill="{color}">{tip}</circle>')
            elif share > 0:
                end = angle + share * 2 * math.pi
                x1, y1 = cx + radius * math.cos(angle), cy + radius * math.sin(angle)
                x2, y2 = cx + radius * math.cos(end), cy + radius * math.sin(end)
                large = 1 if share > 0.5 else 0
                parts.append(f'<path d="M{cx},{cy} L{x1:.2f},{y1:.2f} A{radius},{radius} 0 {large} 1 {x2:.2f},{y2:.2f} Z" fill="{color}">{tip}</path>')
                angle = end
            y = top + 12 + 24 * position
            parts.append(f'<rect x="380" y="{y}" width="12" height="12" fill="{color}"/>'
                         f'<text x="400" y="{y + 11}">{escape(short(label, 40))}: {escape(legend[position])} ({share:.1%})</text>')
    else:
        numbers = [float(row[at]) for row in rows for _, at in series if row[at] is not None]
        ticks = nice_ticks(min(0.0, min(numbers)), max(0.0, max(numbers)))
        low, high = ticks[0], ticks[-1]
        if kind == "horizontal_bar":
            left = min(300, 24 + 7 * max(len(short(label, 40)) for label in labels))
            band = 14 + 12 * len(series)
            plot_width, height = width - left - 32, top + band * len(rows) + 48
            scale = lambda value: left + plot_width * (value - low) / (high - low)
            for tick, text in zip(ticks, tick_texts(ticks)):
                x = scale(tick)
                parts.append(f'<line x1="{x:.2f}" y1="{top}" x2="{x:.2f}" y2="{top + band * len(rows)}" stroke="currentColor" stroke-opacity="0.15"/>'
                             f'<text x="{x:.2f}" y="{top + band * len(rows) + 18}" text-anchor="middle">{escape(text)}</text>')
            for index, label in enumerate(labels):
                y = top + index * band
                parts.append(f'<text x="{left - 8}" y="{y + band / 2 + 4:.2f}" text-anchor="end"><title>{escape(label)}</title>{escape(short(label, 40))}</text>')
                for position, (name, at) in enumerate(series):
                    value = rows[index][at]
                    if value is None:
                        continue
                    x0, x1 = sorted((scale(0.0), scale(float(value))))
                    parts.append(f'<rect x="{x0:.2f}" y="{y + 7 + 12 * position}" width="{max(x1 - x0, 1):.2f}" height="10" fill="{PALETTE[position % len(PALETTE)]}">'
                                 f'<title>{escape(label)}, {escape(name)}: {escape(exact_text(float(value)))}</title></rect>')
        else:
            rotate = len(rows) > 8 or max(len(label) for label in labels) > 12
            bottom = 110 if rotate else 40
            left, height = 72, 440 + (70 if rotate else 0)
            plot_width, plot_height = width - left - 24, height - top - bottom
            band = plot_width / len(rows)
            scale = lambda value: top + plot_height * (1 - (value - low) / (high - low))
            for tick, text in zip(ticks, tick_texts(ticks)):
                y = scale(tick)
                parts.append(f'<line x1="{left}" y1="{y:.2f}" x2="{width - 24}" y2="{y:.2f}" stroke="currentColor" stroke-opacity="0.15"/>'
                             f'<text x="{left - 8}" y="{y + 4:.2f}" text-anchor="end">{escape(text)}</text>')
            every = max(1, math.ceil(len(rows) / 40))
            for index, label in enumerate(labels):
                if index % every:
                    continue
                x, y = left + band * (index + 0.5), top + plot_height + 16
                anchor = f' text-anchor="end" transform="rotate(-40 {x:.2f} {y:.2f})"' if rotate else ' text-anchor="middle"'
                parts.append(f'<text x="{x:.2f}" y="{y:.2f}"{anchor}><title>{escape(label)}</title>{escape(short(label))}</text>')
            for position, (name, at) in enumerate(series):
                color = PALETTE[position % len(PALETTE)]
                if kind == "bar":
                    group = band * 0.8
                    bar = group / len(series)
                    for index, label in enumerate(labels):
                        value = rows[index][at]
                        if value is None:
                            continue
                        y0, y1 = sorted((scale(0.0), scale(float(value))))
                        x = left + band * index + (band - group) / 2 + bar * position
                        parts.append(f'<rect x="{x:.2f}" y="{y0:.2f}" width="{max(bar - 1, 1):.2f}" height="{max(y1 - y0, 1):.2f}" fill="{color}">'
                                     f'<title>{escape(label)}, {escape(name)}: {escape(exact_text(float(value)))}</title></rect>')
                else:
                    path, pen = [], "M"
                    for index, label in enumerate(labels):
                        value = rows[index][at]
                        if value is None:
                            pen = "M"
                            continue
                        x, y = left + band * (index + 0.5), scale(float(value))
                        path.append(f"{pen}{x:.2f},{y:.2f}")
                        pen = "L"
                        if len(rows) <= 40:
                            parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="3" fill="{color}">'
                                         f'<title>{escape(label)}, {escape(name)}: {escape(exact_text(float(value)))}</title></circle>')
                    parts.append(f'<path d="{" ".join(path)}" fill="none" stroke="{color}" stroke-width="2"/>')
    return (f'<figure><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" role="img" aria-labelledby="{title_id}" '
            f'font-family="Segoe UI, Aptos, Calibri, sans-serif" font-size="12" fill="currentColor"><title id="{title_id}">{escape(title)}</title>'
            + "".join(parts) + "</svg></figure>")


def add_excel_chart(worksheet, sheet):
    from openpyxl.chart import BarChart, LineChart, PieChart, Reference
    from openpyxl.utils import get_column_letter
    spec, columns, count = sheet["chart"], sheet["columns"], len(sheet["rows"])
    kind = spec["type"]
    if kind == "pie":
        chart = PieChart()
    elif kind == "line":
        chart = LineChart()
    else:
        chart = BarChart()
        chart.type = "bar" if kind == "horizontal_bar" else "col"
    chart.title = spec.get("title") or sheet.get("name") or "Chart"
    for name in spec["values"]:
        column = columns.index(name) + 1
        chart.add_data(Reference(worksheet, min_col=column, min_row=1, max_row=count + 1), titles_from_data=True)
    label = columns.index(spec["label"]) + 1
    chart.set_categories(Reference(worksheet, min_col=label, min_row=2, max_row=count + 1))
    chart.width, chart.height = 24, 12
    worksheet.add_chart(chart, f"{get_column_letter(len(columns) + 2)}2")


def render(request):
    data = request["data"]
    sheets = data["sheets"]
    if not isinstance(sheets, list) or not 1 <= len(sheets) <= 10:
        raise ValueError("Provide 1 to 10 sheets")
    total = 0
    for number, sheet in enumerate(sheets, 1):
        where = f"Sheet {number} ({sheet.get('name') or 'unnamed'})" if isinstance(sheet, dict) else f"Sheet {number}"
        if not isinstance(sheet, dict) or "columns" not in sheet or "rows" not in sheet:
            raise ValueError(f"{where} needs columns and rows")
        columns, rows = sheet["columns"], sheet["rows"]
        if not isinstance(columns, list) or not 1 <= len(columns) <= 50 or not all(isinstance(column, str) and column for column in columns):
            raise ValueError(f"{where}: provide 1 to 50 named columns")
        if len(set(columns)) != len(columns):
            raise ValueError(f"{where}: column names must be unique")
        if not isinstance(rows, list):
            raise ValueError(f"{where}: rows must be a list of rows")
        for index, row in enumerate(rows, 1):
            if not isinstance(row, list) or len(row) != len(columns):
                count = len(row) if isinstance(row, list) else "no"
                raise ValueError(f"{where}: row {index} has {count} values but the sheet has {len(columns)} columns")
        total += len(rows)
        if total > 5000:
            raise ValueError(f"Reports support at most 5000 total rows; {where} brings the total to {total}")
        declared = int(sheet.get("sourceRowCount", len(rows)))
        if declared != len(rows):
            raise ValueError(f"{where}: {len(rows)} rows were supplied but sourceRowCount says {declared}. If the sheet is text you wrote, leave sourceRowCount out; if it holds query results, supply every one of the {declared} rows")
        for index, row in enumerate(rows, 1):
            for value in row:
                try:
                    safe_cell(value)
                except ValueError as error:
                    raise ValueError(f"{where}: row {index}: {error}") from None
        check_text_and_chart(where, sheet)
    format_name = request["format"]
    if format_name == "csv":
        if len(sheets) != 1:
            raise ValueError("CSV supports one sheet; choose XLSX for a workbook")
        if sheets[0].get("text") or sheets[0].get("chart"):
            raise ValueError("CSV holds one table only; choose HTML or XLSX for a sheet's text or chart")
        buffer = io.StringIO(newline="")
        writer = csv.writer(buffer)
        writer.writerow([safe_cell(value) for value in sheets[0]["columns"]])
        writer.writerows([[safe_cell(value) for value in row] for row in sheets[0]["rows"]])
        content = buffer.getvalue().encode("utf-8-sig")
    elif format_name == "xlsx":
        from openpyxl import Workbook
        from openpyxl.styles import Alignment, Font, PatternFill
        workbook = Workbook()
        workbook.remove(workbook.active)
        for index, sheet in enumerate(sheets):
            name = str(sheet.get("name") or f"Sheet{index + 1}")
            name = "".join(character for character in name if character not in "[]:*?/\\")[:31] or f"Sheet{index + 1}"
            worksheet = workbook.create_sheet(name)
            worksheet.append([safe_cell(value) for value in sheet["columns"]])
            for row in sheet["rows"]:
                worksheet.append([safe_cell(value) for value in row])
            worksheet.freeze_panes = "A2"
            worksheet.auto_filter.ref = worksheet.dimensions
            for cell in worksheet[1]:
                cell.font = Font(bold=True, color="FFFFFF")
                cell.fill = PatternFill("solid", fgColor="404040")
            for column in worksheet.columns:
                worksheet.column_dimensions[column[0].column_letter].width = min(48, max(14, max(len(str(cell.value or "")) for cell in column[:100]) + 2))
            if sheet.get("chart"):
                add_excel_chart(worksheet, sheet)
        notes = [(worksheet.title, sheet["text"]) for worksheet, sheet in zip(workbook.worksheets, sheets) if sheet.get("text")]
        if notes:
            worksheet = workbook.create_sheet("Notes")
            worksheet.append(["Sheet", "Notes"])
            for name, text in notes:
                worksheet.append([safe_cell(name), safe_cell(text)])
            for cell in worksheet[1]:
                cell.font = Font(bold=True, color="FFFFFF")
                cell.fill = PatternFill("solid", fgColor="404040")
            for cell in worksheet["B"][1:]:
                cell.alignment = Alignment(wrap_text=True, vertical="top")
            worksheet.column_dimensions["A"].width = 32
            worksheet.column_dimensions["B"].width = 100
        buffer = io.BytesIO()
        workbook.save(buffer)
        content = buffer.getvalue()
    elif format_name == "html":
        escape = lambda value: html.escape("" if value is None else str(value), quote=True)
        sections = []
        for number, sheet in enumerate(sheets, 1):
            headings = "".join(f'<th scope="col">{escape(column)}</th>' for column in sheet["columns"])
            rows = "".join("<tr>" + "".join(f"<td>{escape(value)}</td>" for value in row) + "</tr>" for row in sheet["rows"])
            text = "".join(f'<p class="text">{escape(line)}</p>' for line in (sheet.get("text") or "").splitlines() if line.strip())
            figure = chart_svg(sheet, number) if sheet.get("chart") else ""
            sections.append(f'<section><h2>{escape(sheet.get("name", "Data"))}</h2>{text}{figure}<p class="count">{len(sheet["rows"])} rows</p><div class="table-scroll"><table><thead><tr>{headings}</tr></thead><tbody>{rows}</tbody></table></div></section>')
        content = ('''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<script>(()=>{const param=new URLSearchParams(window.location.search).get("scoutTheme");const theme=param||(window.matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light");document.documentElement.setAttribute("data-theme",theme)})();</script>
<title>''' + escape(data.get("title", "FinOps Report")) + "</title><style>" + THEME + '''
*{box-sizing:border-box}body{margin:0;padding:24px;font:14px "Segoe UI",Aptos,Calibri,sans-serif;letter-spacing:0;background:var(--cp-bg);color:var(--cp-text)}main{max-width:1440px;margin:auto}h1{font-size:28px;overflow-wrap:anywhere}h2{font-size:20px}p{color:var(--cp-text-muted);overflow-wrap:anywhere}input{width:min(100%,480px);padding:12px;border:1px solid var(--cp-border-strong);background:var(--cp-surface);color:var(--cp-text);border-radius:6px}section{margin-top:28px}p.text{color:var(--cp-text);max-width:960px;line-height:1.5}figure{margin:16px 0;padding:12px;background:var(--cp-surface);border:1px solid var(--cp-border);color:var(--cp-text);max-width:1000px}figure svg{display:block;width:100%;height:auto}.table-scroll{overflow:auto;border:1px solid var(--cp-border)}table{border-collapse:collapse;min-width:100%;background:var(--cp-surface)}th,td{text-align:left;padding:10px 12px;border-bottom:1px solid var(--cp-border);max-width:360px;overflow-wrap:anywhere}th{background:var(--cp-surface-soft);position:sticky;top:0}tr[hidden]{display:none}label{display:block;margin:20px 0 8px}@media print{input,label{display:none}body{padding:0}.table-scroll{overflow:visible}th{position:static}}
</style></head><body><main><h1>''' + escape(data.get("title", "FinOps Report")) + "</h1><p>" + escape(data.get("source", "Source timestamp unknown")) + '''</p><label for="filter">Filter rows</label><input type="search" id="filter" autocomplete="off">''' + "".join(sections) + '''</main><script>document.getElementById('filter').addEventListener('input',event=>{const query=event.target.value.toLowerCase();document.querySelectorAll('section').forEach(section=>{const rows=[...section.querySelectorAll('tbody tr')];rows.forEach(row=>row.hidden=!row.textContent.toLowerCase().includes(query));section.querySelector('.count').textContent=rows.filter(row=>!row.hidden).length+' / '+rows.length+' rows';});});</script></body></html>''').encode("utf-8")
    else:
        raise ValueError("Unsupported report format")
    return {"ok": True, "rows": total, "contentBase64": base64.b64encode(content).decode("ascii")}


if __name__ == "__main__":
    try:
        print(json.dumps(render(json.loads(sys.stdin.read()))))
    except ValueError as error:
        # The message names the sheet, row or limit to fix; it never echoes cell values.
        print(json.dumps({"ok": False, "error": f"Invalid report data: {error}"}))
    except KeyError as error:
        print(json.dumps({"ok": False, "error": f"Invalid report data: missing {error}"}))
    except TypeError:
        print(json.dumps({"ok": False, "error": "Invalid report data: dataJson must be {title, source, sheets:[{name, text, columns, rows, sourceRowCount, chart}]}"}))