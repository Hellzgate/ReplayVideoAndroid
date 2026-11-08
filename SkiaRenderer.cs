// 보드 렌더러(SkiaSharp) — Windows 판(ReplayVideo\Program.cs 의 RenderState)과 같은 레이아웃/규칙.
// GDI+ 대신 Skia 로 그린다. ffmpeg 없이 MediaCodec 으로 인코딩하므로 프레임은 SKBitmap 에 그린다.
using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace ReplayVideoCore
{
    public sealed class SkiaRenderer : IDisposable
    {
        private readonly CardLibrary _lib;

        // 레이아웃 (Windows 판과 동일 값)
        public int W = 1400, H = 820;
        public int CardW = 86, CardH = 123;
        public const int Gap = 8;
        public int FieldX = 176;
        public int FieldZoneX = 82;
        public int OppSzY = 76, OppMzY = 76 + 123 + 6;
        public int EmzY = 468 - 123 - 8;
        public int MyMzY = 468, MySzY = 468 + 123 + 6;
        public bool Portrait;

        private static SKTypeface _tf;

        private readonly Dictionary<string, SKBitmap> _scaled = new Dictionary<string, SKBitmap>();
        private readonly Dictionary<string, SKPaint> _paints = new Dictionary<string, SKPaint>();

        public SkiaRenderer(CardLibrary lib, bool portrait)
        {
            _lib = lib;
            Portrait = portrait;
            SetupLayout(portrait);
            _tf = BuildTypeface();
        }

        private void SetupLayout(bool portrait)
        {
            Portrait = portrait;
            if (portrait)
            {
                W = 1080; H = 1920;
                CardW = 165; CardH = 238;               // 필드마법 존까지 6칸
                FieldZoneX = 25;
                FieldX = FieldZoneX + CardW + Gap;
                OppSzY = 96; OppMzY = OppSzY + CardH + 8;
                EmzY = OppMzY + CardH + 8;
                MyMzY = EmzY + CardH + 8;
                MySzY = MyMzY + CardH + 8;
            }
            else
            {
                W = 1400; H = 820;
                CardW = 86; CardH = 123;
                FieldZoneX = 82;
                FieldX = 176;
                OppSzY = 76; OppMzY = OppSzY + CardH + 6;
                EmzY = OppMzY + CardH + 8;
                MyMzY = EmzY + CardH + 8;
                MySzY = MyMzY + CardH + 6;
            }
            Layout.W = W; Layout.H = H;
            Layout.CardW = CardW; Layout.CardH = CardH; Layout.Gap = Gap;
            Layout.FieldX = FieldX;
            Layout.OppSzY = OppSzY; Layout.OppMzY = OppMzY;
            Layout.MyMzY = MyMzY; Layout.MySzY = MySzY;
            Layout.EmzY = EmzY;
            Layout.FieldZoneX = FieldZoneX;
        }

        // ── 폰트(한글 포함) ───────────────────────────────────
        public static string FontInfo = "";

        private static SKTypeface BuildTypeface()
        {
            // Android 의 NotoSansCJK-Regular.ttc 는 [JP(0), KR(1), SC(2), TC(3)] 순서다.
            // family 이름 조회는 SkiaSharp 에서 실패(null)하고, 글리프 커버리지 API 도 신뢰할 수 없어
            // **KR face 를 직접 지정**한다(한글+영문+숫자 모두 포함).
            var diag = new System.Text.StringBuilder();
            string f = "/system/fonts/NotoSansCJK-Regular.ttc";
            if (File.Exists(f))
            {
                for (int i = 1; i <= 3 && i < 4; i++)
                {
                    try
                    {
                        var tf = SKTypeface.FromFile(f, i);
                        if (tf != null) { FontInfo = "ttc:" + i; return tf; }
                    }
                    catch { diag.Append("ttc" + i + "=ex "); }
                }
                try
                {
                    var tf0 = SKTypeface.FromFile(f);
                    if (tf0 != null) { FontInfo = "ttc:0"; return tf0; }
                }
                catch { }
            }
            try
            {
                var m = SKFontManager.Default.MatchCharacter('한');
                if (m != null) { FontInfo = "match:" + m.FamilyName; return m; }
            }
            catch { }
            foreach (string fam in new[] { "Noto Sans CJK KR", "Noto Sans KR", "sans-serif" })
            {
                try
                {
                    var tf = SKTypeface.FromFamilyName(fam);
                    if (tf != null) { FontInfo = "family:" + fam; return tf; }
                }
                catch { }
            }
            FontInfo = "default(" + diag.ToString().Trim() + ")";
            return SKTypeface.Default;
        }
        // ── 페인트 캐시 ───────────────────────────────────────
        private SKPaint P(float size, bool bold, SKColor color, bool stroke, float strokeW)
        {
            string key = string.Format("{0}|{1}|{2}|{3}|{4}", size, bold ? 1 : 0, color, stroke ? 1 : 0, strokeW);
            SKPaint p;
            if (_paints.TryGetValue(key, out p)) return p;
            p = new SKPaint();
            p.IsAntialias = true;
            p.Typeface = _tf;
            p.TextSize = size;
            p.FakeBoldText = bold;
            p.Color = color;
            p.Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
            p.StrokeWidth = strokeW;
            p.FilterQuality = SKFilterQuality.High;
            _paints[key] = p;
            return p;
        }

        private static SKColor C(int a, int r, int g, int b) { return new SKColor((byte)r, (byte)g, (byte)b, (byte)a); }
        private static SKColor C(int r, int g, int b) { return new SKColor((byte)r, (byte)g, (byte)b, 255); }

        // ── 텍스트 ────────────────────────────────────────────
        private void Text(SKCanvas c, string text, float x, float y, float size, bool bold, SKColor color)
        {
            if (string.IsNullOrEmpty(text)) return;
            SKPaint p = P(size, bold, color, false, 0);
            float baseline = y - p.FontMetrics.Ascent;
            c.DrawText(text, x, baseline, p);
        }

        private float TextW(string text, float size, bool bold)
        {
            SKPaint p = P(size, bold, SKColors.White, false, 0);
            return p.MeasureText(text);
        }

        private float TextH(float size, bool bold)
        {
            SKPaint p = P(size, bold, SKColors.White, false, 0);
            return p.FontMetrics.Descent - p.FontMetrics.Ascent;
        }

        /// <summary>사각형 안에서 줄바꿈해 그린다(GDI DrawString(rect) 근사).</summary>
        private void TextWrap(SKCanvas c, string text, float x, float y, float w, float h, float size, bool bold, SKColor color)
        {
            if (string.IsNullOrEmpty(text)) return;
            SKPaint p = P(size, bold, color, false, 0);
            float lineH = p.FontMetrics.Descent - p.FontMetrics.Ascent + 1;
            float cy = y;
            var sb = new System.Text.StringBuilder();
            foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
            {
                sb.Length = 0;
                foreach (char ch in para)
                {
                    sb.Append(ch);
                    if (p.MeasureText(sb.ToString()) > w - 4)
                    {
                        sb.Length--;
                        if (sb.Length == 0) { sb.Append(ch); }
                        if (cy + lineH > y + h) return;
                        c.DrawText(sb.ToString(), x + 2, cy - p.FontMetrics.Ascent, p);
                        cy += lineH;
                        sb.Length = 0;
                        if (sb.Length == 0 && ch != ' ') sb.Append(ch);
                    }
                }
                if (sb.Length > 0)
                {
                    if (cy + lineH > y + h) return;
                    c.DrawText(sb.ToString(), x + 2, cy - p.FontMetrics.Ascent, p);
                    cy += lineH;
                }
                if (cy + lineH > y + h) return;
            }
        }

        // ── 이미지 ────────────────────────────────────────────
        /// <summary>지정 크기로 축소한 비트맵(캐시). 없으면 null. hi=컷인용(내려받은 고해상도만).</summary>
        private SKBitmap Scaled(int code, int w, int h, bool hi)
        {
            if (code <= 0 || w <= 0 || h <= 0) return null;
            string key = (hi ? "h" : "s") + code + "_" + w + "x" + h;
            SKBitmap done;
            if (_scaled.TryGetValue(key, out done)) return done;

            SKBitmap img = null;
            string path = hi ? _lib.HiImagePath(code) : _lib.ImagePath(code);
            if (path != null)
            {
                try
                {
                    using (SKBitmap bmp = SKBitmap.Decode(path))
                    {
                        if (bmp != null)
                        {
                            SKBitmap dst = bmp.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                            if (dst != null) img = dst;
                        }
                    }
                }
                catch { img = null; }
            }
            if (_scaled.Count < 1500) _scaled[key] = img;
            return img;
        }

        // ── 프레임 그리기 ─────────────────────────────────────
        public void Draw(SKCanvas c, Sim sim, Dictionary<string, Vis> before, float t)
        {
            Dictionary<string, Vis> cur = sim.Snapshot();

            // 배경 그라데이션은 화면 고정 → 한 번만 만들어 재사용(Shader 를 매 프레임 만들면 이중 해제 위험)
            using (var bgp = new SKPaint())
            {
                bgp.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, H),
                    new[] { C(255, 10, 22, 40), C(255, 20, 40, 70) }, null, SKShaderTileMode.Clamp);
                bgp.IsAntialias = false;
                c.DrawRect(0, 0, W, H, bgp);
            }

            SKColor white = C(240, 247, 255), sub = C(150, 175, 205), acc = C(120, 195, 255), red = C(255, 120, 120);

            // 헤더
            Text(c, string.Format("TURN {0}   ·   {1}", sim.Turn, Sim.PhaseName(sim.Phase)), 18, 10, 21, true, white);
            Text(c, sim.Caption ?? "", 300, 14, 16, true, acc);
            if (!string.IsNullOrEmpty(sim.Winner))
                Text(c, sim.Winner, W - 260, 10, 21, true, C(255, 255, 215, 90));

            // LP
            int lpY = 336;
            if (Portrait)
            {
                string lpText = string.Format("상대 LP {0:N0}    내 LP {1:N0}", sim.Lp[1], sim.Lp[0]);
                float lw = TextW(lpText, 15, true);
                Text(c, lpText, W - lw - 18, 12, 15, true, sim.Lp[0] <= 2000 ? red : white);
                if (sim.DmgPlayer >= 0 && sim.DmgValue > 0)
                    Text(c, "-" + sim.DmgValue.ToString("N0"), W - 160, 40, 21, true, red);
            }
            else
            {
                Text(c, "상대 LP  " + sim.Lp[1].ToString("N0"), 8, lpY, 17, true, sim.Lp[1] <= 2000 ? red : white);
                Text(c, "내 LP     " + sim.Lp[0].ToString("N0"), 8, lpY + 36, 17, true, sim.Lp[0] <= 2000 ? red : white);
                if (sim.DmgPlayer >= 0 && sim.DmgValue > 0)
                {
                    int dy = sim.DmgPlayer == 0 ? lpY + 36 : lpY;
                    Text(c, "-" + sim.DmgValue.ToString("N0"), 8, dy + 24, 24, true, red);
                }
            }

            // 존 배경
            DrawZoneRow(c, OppSzY);
            DrawZoneRow(c, OppMzY);
            DrawEmz(c);
            DrawZoneRow(c, MyMzY);
            DrawZoneRow(c, MySzY);
            DrawFieldZone(c, OppSzY);
            DrawFieldZone(c, MySzY);

            // 손패/묘지/제외
            if (Portrait)
            {
                DrawHandH(c, sim, 1, 24, 1432, sub, white);
                DrawHandH(c, sim, 0, 24, 1528, sub, white);
                DrawPile(c, sim, 24, 1640, "내 묘지", sim.Grave[0], sub, white, 66, 94, 6);
                DrawPile(c, sim, 560, 1640, "내 제외", sim.Banished[0], sub, white, 66, 94, 6);
                DrawPile(c, sim, 24, 1748, "상대 묘지", sim.Grave[1], sub, white, 66, 94, 6);
                DrawPile(c, sim, 560, 1748, "상대 제외", sim.Banished[1], sub, white, 66, 94, 6);
            }
            else
            {
                DrawHand(c, sim, 1, 660, 78, sub, white);
                DrawHand(c, sim, 0, 660, 452, sub, white);
                DrawPile(c, sim, 820, OppSzY, "상대 묘지", sim.Grave[1], sub, white, 52, 75, 8);
                DrawPile(c, sim, 820, OppSzY + 128, "상대 제외", sim.Banished[1], sub, white, 52, 75, 8);
                DrawPile(c, sim, 820, MyMzY, "내 묘지", sim.Grave[0], sub, white, 52, 75, 8);
                DrawPile(c, sim, 820, MyMzY + 128, "내 제외", sim.Banished[0], sub, white, 52, 75, 8);
            }

            // 카드(보간)
            sim.RebuildChainMarks();
            var all = new HashSet<string>();
            foreach (var k in cur.Keys) all.Add(k);
            foreach (var k in before.Keys) all.Add(k);

            foreach (string key in all)
            {
                Vis c2, b2;
                cur.TryGetValue(key, out c2);
                before.TryGetValue(key, out b2);
                if (c2 == null && b2 == null) continue;

                float x, y, alpha;
                Vis proto = c2 ?? b2;
                if (c2 != null && b2 != null)
                {
                    x = b2.X + (c2.X - b2.X) * t;
                    y = b2.Y + (c2.Y - b2.Y) * t;
                    alpha = 1f;
                }
                else if (c2 != null) { x = c2.X; y = c2.Y; alpha = t; }
                else { x = b2.X; y = b2.Y; alpha = 1f - t; }

                DrawCard(c, sim, x, y, proto.Code, proto.Pos, alpha,
                    sim.Hi.Contains(key), sim.Tg.Contains(key), key, sim.Reveal.Contains(key), white);
            }

            // 체인 패널 (상단 띠 — EMZ/필드존과 겹치지 않게)
            if (sim.ChainCount > 0)
            {
                int cpx = Portrait ? 24 : 420;
                int cpy = Portrait ? 40 : 42;
                int cpw = Portrait ? W - 48 : W - 440;
                int maxLines = 2;
                int lines = Math.Min(sim.ChainCount, maxLines);
                int cph = 4 + lines * 14 + (sim.ChainCount > lines ? 12 : 0);
                c.DrawRect(cpx, cpy, cpw, cph, P(10, false, C(255, 20, 36, 58), false, 0));
                Text(c, "체인 " + sim.ChainCount, cpx + 6, cpy + 2, 10, true, acc);
                int cy = cpy + 15;
                for (int k = 0; k < lines; k++)
                {
                    Sim.Link lk = sim.ChainAt(k);
                    string mark = lk.Negated ? "X" : (lk.Solving ? "▶" : " ");
                    string nm = _lib.NameOf(lk.Code);
                    string locTag = "[" + Sim.LocName(lk.Loc) + "] ";
                    int maxName = Math.Max(8, (cpw - 24) / 7);
                    if (nm.Length > maxName) nm = nm.Substring(0, maxName - 1) + "…";
                    SKColor col = lk.Negated ? C(255, 255, 120, 120)
                                : (lk.Solving ? C(255, 120, 240, 240) : C(255, 200, 180, 240));
                    Text(c, string.Format("{0} {1}. {2}{3}", mark, lk.N, locTag, nm), cpx + 6, cy, 10, true, col);
                    cy += 14;
                }
                if (sim.ChainCount > lines)
                    Text(c, "외 " + (sim.ChainCount - lines), cpx + 6, cy, 10, true, sub);
            }

            // 카드 설명
            string desc = sim.CurCode > 0 ? _lib.DescOf(sim.CurCode) : null;
            if (!string.IsNullOrEmpty(desc))
            {
                int dy = MySzY + CardH + (Portrait ? 12 : 8);
                int dw = 5 * (CardW + Gap) + 20;
                int dh = Portrait ? 78 : 46;
                c.DrawRect(FieldX, dy, dw, dh, P(10, false, C(255, 18, 32, 52), false, 0));
                string ko = _lib.DescKo(sim.CurCode);
                string body = !string.IsNullOrEmpty(ko) ? ko : desc;
                string t2 = _lib.NameOf(sim.CurCode) + " — " + body.Replace("\r", " ").Replace("\n", " ");
                if (t2.Length > 150) t2 = t2.Substring(0, 149) + "…";
                TextWrap(c, t2, FieldX + 2, dy + 2, dw - 4, dh - 4, 10, false, C(185, 185, 205, 230));
            }

            // 하단 정보
            Text(c, string.Format("패 {0} / {1}     묘지 {2} / {3}     제외 {4} / {5}     덱 {6} / {7}     엑스트라 {8} / {9}",
                sim.Hand[0], sim.Hand[1], sim.Grave[0].Count, sim.Grave[1].Count,
                sim.Banished[0].Count, sim.Banished[1].Count,
                sim.RemainDeck[0], sim.RemainDeck[1], sim.RemainExtra[0], sim.RemainExtra[1]),
                18, H - 26, 10, false, sub);
            if (sim.Grave[0].Count > 0)
                Text(c, "내 묘지 최근: " + _lib.NameOf(sim.Grave[0][sim.Grave[0].Count - 1]), 18, H - 44, 9, false, sub);
            if (sim.Grave[1].Count > 0)
                Text(c, "상대 묘지 최근: " + _lib.NameOf(sim.Grave[1][sim.Grave[1].Count - 1]), W - 420, H - 44, 9, false, sub);

            // 발동 컷인(맨 마지막)
            if (sim.ZoomCode > 0)
            {
                int zw = Portrait ? 520 : 320;
                int zh = (int)Math.Round(zw * 254.0 / 177.0);
                int zx = (W - zw) / 2, zy = (H - zh) / 2;
                c.DrawRect(0, 0, W, H, P(10, false, C(160, 0, 0, 0), false, 0));

                SKBitmap zi = Scaled(sim.ZoomCode, zw, zh, true) ?? Scaled(sim.ZoomCode, zw, zh, false);
                if (zi != null)
                {
                    var d = new SKRect(zx, zy, zx + zw, zy + zh);
                    c.DrawBitmap(zi, d, P(10, false, SKColors.White, false, 0));
                }
                else
                    c.DrawRect(zx, zy, zw, zh, P(10, false, C(255, 40, 60, 90), false, 0));

                c.DrawRect(zx, zy, zw, zh, P(10, false, C(255, 255, 215, 90), true, 5));

                float fs = Portrait ? 22 : 16;
                string t3 = "발동 !   " + _lib.NameOf(sim.ZoomCode);
                float tw = TextW(t3, fs, true), th = TextH(fs, true);
                c.DrawRect(zx - 8, zy - th - 10, zw + 16, th + 8, P(10, false, C(200, 20, 14, 0), false, 0));
                Text(c, t3, zx, zy - th - 6, fs, true, C(255, 255, 235, 150));
            }
        }

        private int ZoneX(int seq) { return FieldX + seq * (CardW + Gap); }

        private void DrawEmz(SKCanvas c)
        {
            using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = C(255, 24, 20, 44) })
            using (var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = C(175, 150, 125, 220) })
            {
                pen.PathEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0);
                int[] cols = { Layout.EmzColLeft, Layout.EmzColRight };
                foreach (int col in cols)
                {
                    int x = ZoneX(col);
                    c.DrawRect(x, EmzY, CardW - 1, CardH - 1, fill);
                    c.DrawRect(x, EmzY, CardW - 1, CardH - 1, pen);
                    float tw = TextW("EMZ", 9, false), th = TextH(9, false);
                    Text(c, "EMZ", x + (CardW - tw) / 2, EmzY + (CardH - th) / 2, 9, false, C(120, 110, 155));
                }
            }
        }

        private void DrawFieldZone(SKCanvas c, int y)
        {
            using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = C(255, 18, 30, 22) })
            using (var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = C(200, 95, 140, 95) })
            {
                c.DrawRect(FieldZoneX, y, CardW - 1, CardH - 1, fill);
                c.DrawRect(FieldZoneX, y, CardW - 1, CardH - 1, pen);
                float tw = TextW("필드", 9, false), th = TextH(9, false);
                Text(c, "필드", FieldZoneX + (CardW - tw) / 2, y + (CardH - th) / 2, 9, false, C(125, 155, 125));
            }
        }

        private void DrawZoneRow(SKCanvas c, int y)
        {
            SKPaint pen = P(10, false, C(255, 38, 60, 88), true, 1);
            for (int i = 0; i < 5; i++)
                c.DrawRect(ZoneX(i), y, CardW - 1, CardH - 1, pen);
        }

        private void DrawHand(SKCanvas c, Sim sim, int pl, int x, int y, SKColor sub, SKColor white)
        {
            List<int> hand = sim.HandCards[pl];
            Text(c, string.Format("{0} 패  {1}장", pl == 0 ? "내" : "상대", hand.Count), x, y - 18, 10, false, sub);
            int cols = 3, tw = 40, th = 57, gapx = 3, gapy = 3;
            int shown = Math.Min(hand.Count, 15);
            for (int k = 0; k < shown; k++)
            {
                int cx = x + (k % cols) * (tw + gapx);
                int cy = y + (k / cols) * (th + gapy);
                DrawThumb(c, cx, cy, tw, th, hand[k], white);
            }
            if (hand.Count > shown)
                Text(c, "외 " + (hand.Count - shown), x, y + 5 * (th + gapy) + 2, 9, false, sub);
        }

        private void DrawHandH(SKCanvas c, Sim sim, int pl, int x, int y, SKColor sub, SKColor white)
        {
            List<int> hand = sim.HandCards[pl];
            Text(c, string.Format("{0} 패  {1}장", pl == 0 ? "내" : "상대", hand.Count), x, y - 18, 10, false, sub);
            int tw = Portrait ? 58 : 46, th = Portrait ? 83 : 66, gapx = Portrait ? 6 : 5;
            int shown = Math.Min(hand.Count, 16);
            for (int k = 0; k < shown; k++)
                DrawThumb(c, x + k * (tw + gapx), y, tw, th, hand[k], white);
            if (hand.Count > shown)
                Text(c, "외 " + (hand.Count - shown), x + shown * (tw + gapx) + 2, y + th / 2, 9, false, sub);
        }

        private void DrawPile(SKCanvas c, Sim sim, int x, int y, string title, List<int> pile, SKColor sub, SKColor white,
                              int tw, int th, int maxShow)
        {
            Text(c, string.Format("{0}  {1}장", title, pile.Count), x, y - 18, 10, false, sub);
            int gapx = 4;
            int shown = Math.Min(pile.Count, maxShow);
            for (int k = 0; k < shown; k++)
            {
                int idx = pile.Count - 1 - k;
                DrawThumb(c, x + k * (tw + gapx), y, tw, th, pile[idx], white);
            }
            if (pile.Count > shown)
                Text(c, "외 " + (pile.Count - shown), x + shown * (tw + gapx) + 2, y + th / 2, 9, false, sub);
        }

        private void DrawThumb(SKCanvas c, int x, int y, int w, int h, int code, SKColor white)
        {
            SKBitmap img = Scaled(code, w, h, false);
            if (img != null)
            {
                var d = new SKRect(x, y, x + w, y + h);
                c.DrawBitmap(img, d, P(10, false, SKColors.White, false, 0));
            }
            else
            {
                c.DrawRect(x, y, w, h, P(10, false, C(255, 34, 52, 78), false, 0));
                TextWrap(c, _lib.NameOf(code), x + 1, y + 2, w - 2, h - 4, 9, false, white);
            }
            c.DrawRect(x, y, w - 1, h - 1, P(10, false, C(255, 70, 105, 150), true, 1));
        }

        private void DrawCard(SKCanvas c, Sim sim, float x, float y, int code, int pos, float alpha,
                              bool hi, bool target, string key, bool reveal, SKColor white)
        {
            if (alpha <= 0.02f) return;
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            bool faceDown = (pos & 0x2) != 0 || (pos & 0x8) != 0;

            if (alpha < 1f)
            {
                c.DrawRect(ix, iy, CardW, CardH, P(10, false, C(255, 30, 48, 72), false, 0));
            }
            else if (faceDown)
            {
                c.DrawRect(ix, iy, CardW, CardH, P(10, false, C(255, 26, 44, 74), false, 0));
                c.DrawRect(ix + 8, iy + 10, CardW - 17, CardH - 21, P(10, false, C(255, 70, 110, 170), true, 1));
                Text(c, "SET", ix + CardW / 2 - 12, iy + CardH / 2 - 6, 10, true, C(255, 120, 165, 220));
            }
            else
            {
                SKBitmap img = Scaled(code, CardW, CardH, false);
                if (img != null)
                {
                    var d = new SKRect(ix, iy, ix + CardW, iy + CardH);
                    c.DrawBitmap(img, d, P(10, false, SKColors.White, false, 0));
                }
                else
                {
                    c.DrawRect(ix, iy, CardW, CardH, P(10, false, C(255, 34, 52, 78), false, 0));
                    TextWrap(c, _lib.NameOf(code), ix + 2, iy + 4, CardW - 4, CardH - 24, 9, false, white);
                }

                // 이름 + ATK
                string nm = _lib.NameOf(code);
                if (nm.Length > 22) nm = nm.Substring(0, 21) + "…";
                c.DrawRect(ix, iy + CardH - 15, CardW, 15, P(10, false, C(210, 8, 18, 30), false, 0));
                TextWrap(c, nm, ix + 1, iy + CardH - 15, CardW - 2, 14, 9, false, white);

                int atk = _lib.AtkOf(code);
                if (atk >= 0)
                {
                    c.DrawRect(ix + CardW - 34, iy + 2, 32, 12, P(10, false, C(200, 12, 24, 40), false, 0));
                    Text(c, atk.ToString("N0"), ix + CardW - 32, iy + 2, 9, true, white);
                }
            }

            if (hi) c.DrawRect(ix, iy, CardW - 1, CardH - 1, P(10, false, C(255, 255, 120, 120), true, 3));
            else if (target) c.DrawRect(ix, iy, CardW - 1, CardH - 1, P(10, false, C(255, 255, 190, 90), true, 3));
            else c.DrawRect(ix, iy, CardW - 1, CardH - 1, P(10, false, C(255, 90, 130, 180), true, 1));

            if (reveal)
            {
                c.DrawRect(ix, iy, CardW - 1, CardH - 1, P(10, false, C(255, 255, 215, 90), true, 3.5f));
                c.DrawRect(ix + CardW - 46, iy + 2, 44, 14, P(10, false, C(235, 70, 50, 0), false, 0));
                Text(c, "발동", ix + CardW - 42, iy + 2, 10, true, C(255, 255, 235, 150));
            }

            Sim.Link lk = sim.ChainMark(key);
            if (lk != null)
            {
                SKColor bc = lk.Negated ? C(255, 255, 90, 90)
                           : lk.Solving ? C(255, 80, 230, 230)
                           : C(255, 190, 130, 255);
                c.DrawRect(ix, iy, CardW - 1, CardH - 1, P(10, false, bc, true, 3));
                c.DrawRect(ix + 2, iy + 2, 22, 14, P(10, false, C(230, 40, 20, 60), false, 0));
                Text(c, "C" + lk.N, ix + 3, iy + 2, 11, true, bc);
                if (lk.Negated)
                {
                    SKPaint pen = P(10, false, C(255, 255, 90, 90), true, 3);
                    c.DrawLine(ix + 4, iy + 4, ix + CardW - 5, iy + CardH - 5, pen);
                    c.DrawLine(ix + CardW - 5, iy + 4, ix + 4, iy + CardH - 5, pen);
                }
            }
        }

        public void Dispose()
        {
            foreach (var p in _paints.Values) { try { p.Dispose(); } catch { } }
            foreach (var i in _scaled.Values) { try { if (i != null) i.Dispose(); } catch { } }
            _paints.Clear(); _scaled.Clear();
        }
    }
}
