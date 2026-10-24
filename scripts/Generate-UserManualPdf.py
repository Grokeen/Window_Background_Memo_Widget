from pathlib import Path
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import Image, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

ROOT = Path(__file__).resolve().parents[1]
ASSET = ROOT / "docs" / "screenshots" / "candidate-01"
OUT = ROOT / "output" / "pdf" / "Notedex_Notes_User_Manual_KO.pdf"
FONT = Path(r"C:\Windows\Fonts\malgun.ttf")
FONT_BOLD = Path(r"C:\Windows\Fonts\malgunbd.ttf")
pdfmetrics.registerFont(TTFont("Malgun", str(FONT)))
pdfmetrics.registerFont(TTFont("Malgun-Bold", str(FONT_BOLD)))
pdfmetrics.registerFontFamily("Malgun", normal="Malgun", bold="Malgun-Bold", italic="Malgun", boldItalic="Malgun-Bold")

INK = colors.HexColor("#282A36")
PURPLE = colors.HexColor("#8B5CF6")
LAVENDER = colors.HexColor("#E6D7FF")
PANEL = colors.HexColor("#F1EEFA")
MUTED = colors.HexColor("#5F6070")

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name="TitleKo", fontName="Malgun-Bold", fontSize=25, leading=31, textColor=INK, spaceAfter=4*mm))
styles.add(ParagraphStyle(name="SubtitleKo", fontName="Malgun", fontSize=10, leading=15, textColor=MUTED, spaceAfter=5*mm))
styles.add(ParagraphStyle(name="H1Ko", fontName="Malgun-Bold", fontSize=18, leading=24, textColor=INK, spaceBefore=2*mm, spaceAfter=3*mm))
styles.add(ParagraphStyle(name="H2Ko", fontName="Malgun-Bold", fontSize=12, leading=17, textColor=PURPLE, spaceBefore=3*mm, spaceAfter=2*mm))
styles.add(ParagraphStyle(name="BodyKo", fontName="Malgun", fontSize=9, leading=14, textColor=INK, spaceAfter=2.2*mm))
styles.add(ParagraphStyle(name="SmallKo", fontName="Malgun", fontSize=7.5, leading=10.5, textColor=MUTED, spaceAfter=1.2*mm))
styles.add(ParagraphStyle(name="CalloutKo", fontName="Malgun-Bold", fontSize=9, leading=13.5, textColor=INK))
styles.add(ParagraphStyle(name="TableKo", fontName="Malgun", fontSize=7.5, leading=10, textColor=INK))
styles.add(ParagraphStyle(name="TableHeadKo", fontName="Malgun-Bold", fontSize=7.7, leading=10.2, textColor=INK))
styles.add(ParagraphStyle(name="CenterSmallKo", fontName="Malgun", fontSize=7.5, leading=10.2, textColor=MUTED, alignment=TA_CENTER))

P = lambda text, style="BodyKo": Paragraph(text, styles[style])

def image(path, maxw, maxh):
    im = Image(str(ASSET / path))
    im._restrictSize(maxw, maxh)
    im.hAlign = "CENTER"
    return im

def image_caption(path, caption, maxw, maxh):
    return [image(path, maxw, maxh), Spacer(1, 1.5*mm), P(caption, "CenterSmallKo")]

def footer(canvas, doc):
    canvas.saveState()
    w, h = A4
    canvas.setStrokeColor(LAVENDER)
    canvas.setLineWidth(0.5)
    canvas.line(16*mm, 13*mm, w-16*mm, 13*mm)
    canvas.setFont("Malgun", 7)
    canvas.setFillColor(MUTED)
    canvas.drawString(16*mm, 8.5*mm, "Notedex · 사용자 매뉴얼 · 0.3.0 개발 검증 빌드 · 2026-10-24")
    canvas.drawRightString(w-16*mm, 8.5*mm, str(doc.page))
    canvas.restoreState()

OUT.parent.mkdir(parents=True, exist_ok=True)
doc = SimpleDocTemplate(str(OUT), pagesize=A4, rightMargin=16*mm, leftMargin=16*mm, topMargin=15*mm, bottomMargin=19*mm, title="Notedex Markdown 메모 사용자 매뉴얼", author="Notedex Project Team", subject="버전 0.3.0 개발 검증 빌드의 사용자 흐름과 검증 범위")
story = []

# Page 1: product overview and the actual manager UI.
story += [P("Notedex", "TitleKo"), P("바탕화면 메모를 내 Markdown 파일로 · 사용자 매뉴얼 · 0.3.0 개발 검증 빌드 · 2026.10.24", "SubtitleKo")]
story += [P("메모 쓰기는 가볍게, 원문은 직접 보관하세요. Notedex는 메모 목록·편집기와 별도 위젯을 연결하고, 본문을 NoteId별 Markdown 파일로 저장합니다. 폴더·태그·체크리스트·중요도는 필요한 순간에 쓸 수 있습니다.", "BodyKo")]
callout = Table([[P("Markdown 파일 원본", "CalloutKo"), P("목록에서 바로 편집", "CalloutKo"), P("메모별 색상·글자 크기", "CalloutKo")]], colWidths=[56*mm, 56*mm, 56*mm])
callout.setStyle(TableStyle([("BACKGROUND", (0,0), (-1,-1), PANEL), ("BOX", (0,0), (-1,-1), 0.6, LAVENDER), ("INNERGRID", (0,0), (-1,-1), 0.4, LAVENDER), ("VALIGN", (0,0), (-1,-1), "MIDDLE"), ("LEFTPADDING", (0,0), (-1,-1), 3*mm), ("RIGHTPADDING", (0,0), (-1,-1), 3*mm), ("TOPPADDING", (0,0), (-1,-1), 3*mm), ("BOTTOMPADDING", (0,0), (-1,-1), 3*mm)]))
story += [callout, Spacer(1, 5*mm), image("notes-manager-wide.png", 180*mm, 112*mm), Spacer(1, 1.5*mm), P("그림 1. 실제 0.3.0 WPF 관리 창 캡처. 합성 예시 자료이며 Windows 바탕화면 실기 캡처는 아닙니다.", "CenterSmallKo"), Spacer(1, 3*mm), P("탐색·메모 목록·편집 영역을 한 화면에 두고, 선택한 메모를 바로 작성합니다. 관리 동작은 목록이나 더보기 메뉴에서 접근합니다.", "BodyKo"), PageBreak()]

# Page 2: narrow workspace and separate widget.
story += [P("작은 화면에서도 이어서 쓰기", "H1Ko"), P("좁은 창에서는 목록과 선택 메모 편집 화면을 오갑니다. 위젯은 별도 창으로 열리며 같은 메모 문서 세션을 사용합니다.", "BodyKo")]
left = [image("notes-manager-compact.png", 82*mm, 108*mm), Spacer(1, 2*mm), P("그림 2. 좁은 창의 편집 흐름과 저장 영역.", "CenterSmallKo")]
right = [image("notes-widget.png", 82*mm, 108*mm), Spacer(1, 2*mm), P("그림 3. 체크리스트를 표시하는 별도 메모 위젯.", "CenterSmallKo")]
tab = Table([[left, right]], colWidths=[87*mm, 87*mm])
tab.setStyle(TableStyle([("VALIGN", (0,0), (-1,-1), "TOP"), ("LEFTPADDING", (0,0), (-1,-1), 1*mm), ("RIGHTPADDING", (0,0), (-1,-1), 1*mm)]))
story += [tab, Spacer(1, 5*mm), P("편집 도구와 저장", "H2Ko"), P("굵게·기울임·목록·체크리스트를 시각 편집 도구로 입력하고, 필요할 때 더보기 메뉴에서 Markdown 원문 모드와 제목·내어쓰기 명령을 사용합니다. 체크박스 변경은 Markdown task marker에 반영됩니다.", "BodyKo"), P("저장 완료 상태는 파일 커밋 뒤 표시됩니다. 입력이 멈춘 뒤 약 600ms 또는 연속 편집 약 4.5초 뒤 자동 저장을 시작합니다. 이 값은 시작 조건이며 디스크 저장 완료 시간을 보장하지 않습니다.", "BodyKo"), PageBreak()]

# Page 3: appearance and organization controls.
story += [P("메모별 모양과 정리", "H1Ko"), P("기본 Dracula 테마 안에서 메모별 배경색과 글자 크기를 바꿀 수 있습니다. 폴더와 태그는 분류에, 고정은 빠른 접근에, 중요도는 일반·중요·긴급 우선순위에 사용합니다.", "BodyKo")]
cols = [[image("notes-appearance.png", 82*mm, 64*mm), Spacer(1, 2*mm), P("그림 4. 메모 배경색·글자 크기 적용 예.", "CenterSmallKo")], [image("notes-settings.png", 82*mm, 64*mm), Spacer(1, 2*mm), P("그림 5. 탐색·폴더·태그 및 설정 영역.", "CenterSmallKo")]]
tab = Table([cols], colWidths=[87*mm, 87*mm])
tab.setStyle(TableStyle([("VALIGN", (0,0), (-1,-1), "TOP"), ("LEFTPADDING", (0,0), (-1,-1), 1*mm), ("RIGHTPADDING", (0,0), (-1,-1), 1*mm)]))
story += [Spacer(1, 3*mm), tab, Spacer(1, 4*mm), P("중요도와 고정은 다릅니다", "H2Ko"), P("고정은 목록에서 메모를 빠르게 찾게 합니다. 중요도는 업무 우선순위를 나타냅니다. 둘 다 창을 항상 위에 두지 않습니다. 위젯 숨김과 관리 창 편집 권한도 별도입니다.", "BodyKo"), P("메모를 휴지통으로 이동한 뒤 복구할 수 있습니다. 폴더·태그·중요도·외형 등 보조 정보는 Markdown 본문이 아닌 workspace sidecar에 저장됩니다.", "BodyKo"), PageBreak()]

# Page 4: file model and legacy import/backup.
story += [P("Markdown 원문과 기존 자료", "H1Ko"), P("기본 저장 위치는 사용자 프로필의 `%LOCALAPPDATA%\\Notedex\\Notes\\markdown-v1\\`입니다. 본문은 메모별 파일, 분류·배치·외형은 별도 workspace sidecar에 둡니다.", "BodyKo")]
code = Table([[P("markdown-v1/notes/&lt;NoteId&gt;.md  — UTF-8 Markdown 본문 원본<br/>markdown-v1/.widget/workspace.json  — 제목·폴더·태그·고정·중요도·배치·외형<br/>.trash/ · .backups/ · .conflicts/  — 휴지통·백업·충돌 보존 자료", "TableKo")]], colWidths=[174*mm])
code.setStyle(TableStyle([("BACKGROUND", (0,0), (-1,-1), PANEL), ("BOX", (0,0), (-1,-1), 0.6, LAVENDER), ("LEFTPADDING", (0,0), (-1,-1), 4*mm), ("TOPPADDING", (0,0), (-1,-1), 3*mm), ("BOTTOMPADDING", (0,0), (-1,-1), 3*mm)]))
story += [code, Spacer(1, 4*mm), image("notes-tags-long.png", 164*mm, 91*mm), Spacer(1, 1.5*mm), P("그림 6. 태그가 많은 경우도 한 줄 요약으로 표시하고 전체 값은 보조 설명에서 확인합니다.", "CenterSmallKo"), Spacer(1, 3*mm), P("기존 SQLite/JSON은 자동 변환하지 않습니다. 가져오기 미리보기에서 파일 종류와 메모 수를 확인하고 별도 확인 대화상자에서 승인해야 합니다. 취소하면 변환하지 않으며 원본을 유지합니다.", "BodyKo"), P("수동 백업 archive는 Markdown 파일 원본 바이트와 sidecar를 함께 담습니다. 격리 테스트에서 정상 복원, 손상 archive 거절과 원본 보존을 검사했습니다. 실제 사용자 자료 변환이나 복원은 수행하지 않았습니다.", "BodyKo"), PageBreak()]

# Page 5: evidence and limitations.
story += [P("검증 결과와 사용 범위", "H1Ko"), P("2026-10-24 0.3.0 개발 후보로 격리 합성 자료를 검사했습니다.", "BodyKo")]
rows = [[P("검증 항목", "TableHeadKo"), P("결과", "TableKo")], [P("개발 빌드", "TableKo"), P("경고 0 · 오류 0", "TableKo")], [P("통합 회귀", "TableKo"), P("12/12 그룹 PASS · Markdown 관련 141 검사", "TableKo")], [P("실제 WPF UI", "TableKo"), P("UI probe PASS · 156개 기록 항목(진단 포함) · compact 본문 399 DIP", "TableKo")], [P("검색/좁은 창", "TableKo"), P("50·100개×10,000자 검색 p95 112.72/115.79ms · 전체 검색 p95 127.22ms", "TableKo")], [P("안내 시작/재시작", "TableKo"), P("도움말 갱신 후 격리 프로필 6회 실행·정상 종료 PASS. 로그인 자동 시작은 미검증.", "TableKo")], [P("성능 목표", "TableKo"), P("직전 측정은 154.0/190.7 MiB로 150 MiB 한도 초과. 현재 후보 메모리는 재측정하지 않아 출시 gate 미통과.", "TableKo")]]
tab = Table(rows, colWidths=[46*mm, 128*mm], repeatRows=1)
tab.setStyle(TableStyle([("BACKGROUND", (0,0), (-1,0), PANEL), ("GRID", (0,0), (-1,-1), 0.4, LAVENDER), ("VALIGN", (0,0), (-1,-1), "TOP"), ("LEFTPADDING", (0,0), (-1,-1), 2.5*mm), ("RIGHTPADDING", (0,0), (-1,-1), 2.5*mm), ("TOPPADDING", (0,0), (-1,-1), 2*mm), ("BOTTOMPADDING", (0,0), (-1,-1), 2*mm)]))
story += [tab, Spacer(1, 6*mm), P("검증하지 않은 범위", "H2Ko"), P("실제 한국어 IME, Windows DPI·복수 모니터, Explorer/Win+D/절전 복귀, 8시간 안정성을 장비에서 검사하지 않았습니다. 테스트는 격리 합성 자료를 사용했으며 실제 사용자 저장소를 열거나 자동 시작 설정을 변경하지 않았습니다.", "BodyKo"), P("PDF 안내는 첫 실행 자동 표시, 다시 보지 않기 설정 저장, 관리 창·트레이 수동 재열기를 지원합니다. 매뉴얼 이미지 갱신 후 격리 프로필 6회 시작·정상 종료를 확인했습니다. 메모리 gate와 실제 Windows 셸·IME·다중 모니터 검증은 미완료입니다. 기능 검사 PASS는 출시 승인이나 호환성 보장이 아닙니다. 서명된 설치본과 GitHub Release는 제공하지 않습니다.", "BodyKo"), Spacer(1, 4*mm), P("문서 기준: 0.3.0 개발 후보 · 저장소의 Markdown 사용자 매뉴얼 원고 참고", "SmallKo")]

doc.build(story, onFirstPage=footer, onLaterPages=footer)
print(OUT)
