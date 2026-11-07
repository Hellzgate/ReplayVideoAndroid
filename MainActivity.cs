using System;
using System.IO;
using Path = System.IO.Path;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Android.Net;
using SkiaSharp;
using WindBotAIEditor.Services;
using ReplayVideoCore;

namespace ReplayVideoAndroid
{
    [Activity(Label = "리플레이 영상", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar", Name = "com.hellzgate.replayvideo.MainActivity")]
    public sealed class MainActivity : Activity
    {
        private const int ReqPick = 1001;

        private TextView _info, _status, _log;
        private ProgressBar _bar;
        private Button _pick, _render, _cancel, _share;
        private Spinner _fps, _layout;

        private string _replayPath;
        private string _outPath;
        private Android.Net.Uri _savedUri;
        private volatile bool _busy, _cancelRequested;
        private int _fpsVal = 12;
        private bool _portrait = true;

        private static readonly Color Bg = Color.Rgb(0x1e, 0x1f, 0x22);
        private static readonly Color Fg = Color.Rgb(0xe6, 0xe8, 0xeb);
        private static readonly Color Sub = Color.Rgb(0x96, 0x9c, 0xa5);
        private static readonly Color Accent = Color.Rgb(0x56, 0x9c, 0xd6);

        /// <summary>2026-11-07: 상태바·액션바까지 앱 화면과 같은 다크 톤으로.</summary>
        private void ApplyWindowTheme()
        {
            try
            {
                Window.SetStatusBarColor(Color.Rgb(0x26, 0x28, 0x2c));
                Window.SetNavigationBarColor(Color.Rgb(0x26, 0x28, 0x2c));
                if (ActionBar != null)
                {
                    ActionBar.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Rgb(0x26, 0x28, 0x2c)));
                    var t = new Android.Text.SpannableString(ActionBar.Title ?? "ReplayVideo");
                    t.SetSpan(new Android.Text.Style.ForegroundColorSpan(Fg), 0, t.Length(),
                        Android.Text.SpanTypes.ExclusiveExclusive);
                    ActionBar.TitleFormatted = t;
                }
            }
            catch { }
        }

        protected override void OnCreate(Bundle saved)
        {
            base.OnCreate(saved);

            ApplyWindowTheme();

            var root = new LinearLayout(this);
            root.Orientation = Orientation.Vertical;
            root.SetBackgroundColor(Bg);
            int pad = Dp(14);
            root.SetPadding(pad, pad, pad, pad);

            var title = new TextView(this) { Text = "리플레이(.yrpX) → 영상(MP4)" };
            title.SetTextColor(Fg);
            title.SetTextSize(Android.Util.ComplexUnitType.Sp, 20);
            title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
            root.AddView(title);

            _pick = MakeButton("1. 리플레이 선택 (.yrpX)");
            _pick.Click += OnPick;
            root.AddView(_pick);

            _info = new TextView(this) { Text = "선택된 파일 없음" };
            _info.SetTextColor(Sub);
            _info.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
            _info.SetPadding(0, Dp(6), 0, Dp(10));
            root.AddView(_info);

            var row = new LinearLayout(this);
            row.Orientation = Orientation.Horizontal;

            var l1 = new TextView(this) { Text = "fps " };
            l1.SetTextColor(Sub);
            row.AddView(l1);

            _fps = new Spinner(this);
            var fa = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
                new[] { "8", "12", "15", "24" });
            fa.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _fps.Adapter = fa;
            _fps.SetSelection(1);
            row.AddView(_fps);

            var l2 = new TextView(this) { Text = "   화면 " };
            l2.SetTextColor(Sub);
            row.AddView(l2);

            _layout = new Spinner(this);
            var la = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
                new[] { "세로 1080x1920", "가로 1400x820" });
            la.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _layout.Adapter = la;
            _layout.SetSelection(0);
            row.AddView(_layout);

            root.AddView(row);

            _render = MakeButton("2. 영상 만들기", true);
            _render.Click += OnRender;
            root.AddView(_render);

            _cancel = MakeButton("취소");
            _cancel.Enabled = false;
            _cancel.Click += delegate { _cancelRequested = true; SetStatus("취소 요청…"); };
            root.AddView(_cancel);

            _share = MakeButton("공유 / 열기");
            _share.Enabled = false;
            _share.Click += OnShare;
            root.AddView(_share);

            _bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
            _bar.Max = 100;
            _bar.Progress = 0;
            root.AddView(_bar);

            _status = new TextView(this) { Text = "대기 중" };
            _status.SetTextColor(Sub);
            _status.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
            _status.SetPadding(0, Dp(4), 0, Dp(6));
            root.AddView(_status);

            var scroll = new ScrollView(this);
            _log = new TextView(this);
            _log.SetTextColor(Color.Rgb(0xc8, 0xd2, 0xdc));
            _log.SetTextSize(Android.Util.ComplexUnitType.Sp, 10);
            _log.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            scroll.AddView(_log);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
            root.AddView(scroll, lp);

            SetContentView(root);

            // 2026-11-07: 진단 로그는 테스트 훅에서만. 일반 실행은 안내 한 줄.
            LogLine("준비 완료 — [1. 리플레이 선택] 으로 시작하세요.");

            // 검증용: adb 로 특정 리플레이를 바로 렌더
            //   am start -n com.hellzgate.replayvideo/.MainActivity --es testReplay <경로> [--ei fps 12] [--es layout landscape]
            try
            {
                string test = Intent.GetStringExtra("testReplay");
                string extDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
                if (!string.IsNullOrEmpty(test))
                {
                    // adb 훅으로 실행했을 때만 진단 (일반 사용자 화면에는 안 남긴다)
                    LogLine("테스트 훅: extra=" + test);
                    LogLine("외부 폴더: " + extDir);
                    LogLine("Skia 폰트: " + (SkiaRenderer.FontInfo.Length > 0 ? SkiaRenderer.FontInfo : "?"));
                    if (!test.StartsWith("/")) test = System.IO.Path.Combine(extDir, test);
                    LogLine("테스트 훅: exists=" + File.Exists(test));
                }
                if (!string.IsNullOrEmpty(test) && File.Exists(test))
                {
                    _replayPath = test;
                    _info.Text = "테스트: " + test;
                    int f = Intent.GetIntExtra("fps", 12);
                    _fpsVal = f;
                    _fps.SetSelection(f == 8 ? 0 : (f == 12 ? 1 : (f == 15 ? 2 : 3)));
                    bool land = Intent.GetStringExtra("layout") == "landscape";
                    _layout.SetSelection(land ? 1 : 0);
                    new Handler(Looper.MainLooper).PostDelayed(delegate { OnRender(null, EventArgs.Empty); }, 1200);
                }
            }
            catch (Exception ex) { LogLine("테스트 훅 오류: " + ex.Message); }
        }

        private Button MakeButton(string text, bool primary = false)
        {
            var b = new Button(this);
            b.Text = text;
            b.SetAllCaps(false);
            b.SetTextColor(primary ? Color.White : Fg);
            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(primary ? Accent : Color.Rgb(0x34, 0x37, 0x3c));
            bg.SetCornerRadius(Dp(10));
            bg.SetStroke(Dp(1), primary ? Accent : Color.Rgb(0x46, 0x4a, 0x52));
            b.Background = bg;
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46));
            lp.TopMargin = Dp(6);
            b.LayoutParameters = lp;
            return b;
        }

        private int Dp(int v)
        {
            return (int)(v * Resources.DisplayMetrics.Density);
        }

        // ── 파일 선택 ─────────────────────────────────────────
        private void OnPick(object sender, EventArgs e)
        {
            if (_busy) return;
            try
            {
                var intent = new Intent(Intent.ActionOpenDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType("*/*");
                StartActivityForResult(intent, ReqPick);
            }
            catch (Exception ex) { SetStatus("파일 선택 실패: " + ex.Message); }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqPick || resultCode != Result.Ok || data == null || data.Data == null) return;
            try
            {
                string dir = Path.Combine(CacheDir.AbsolutePath, "in");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "replay.yrpX");
                using (var ins = ContentResolver.OpenInputStream(data.Data))
                using (var outs = File.Create(dst))
                    ins.CopyTo(outs);
                _replayPath = dst;
                _share.Enabled = false;
                _savedUri = null;
                _info.Text = "선택: " + (data.Data.LastPathSegment ?? "replay") + "\n" + dst;
                LogLine("리플레이 준비: " + new FileInfo(dst).Length + "B");
            }
            catch (Exception ex) { SetStatus("파일을 읽을 수 없습니다: " + ex.Message); }
        }

        // ── 렌더 ──────────────────────────────────────────────
        private void OnRender(object sender, EventArgs e)
        {
            LogLine("OnRender: busy=" + _busy + " path=" + (_replayPath ?? "(null)"));
            if (_busy) return;
            if (_replayPath == null || !File.Exists(_replayPath)) { SetStatus("먼저 리플레이를 선택하세요"); return; }

            _busy = true; _cancelRequested = false;
            _render.Enabled = false; _pick.Enabled = false; _cancel.Enabled = true; _share.Enabled = false;
            _bar.Progress = 0;
            _fpsVal = int.Parse((string)_fps.SelectedItem);
            _portrait = _layout.SelectedItemPosition == 0;

            Task.Run(delegate { RunRender(); });
        }

        private void RunRender()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string root = Path.Combine(FilesDir.AbsolutePath, "rv");
                var lib = new CardLibrary(root, null) { ApiEnabled = true, TranslateEnabled = true, Log = LogLine };
                LogLine("리플레이 파싱…");
                ReplayFile rep = ReplayParser.Parse(_replayPath);
                LogLine(string.Format("{0} 패킷 · {1} 턴", rep.PacketCount, rep.TurnCount));

                SetStatus("카드 정보 받는 중…");
                lib.Prefetch(rep, delegate (int d, int t) { SetProgress(d * 15 / Math.Max(1, t)); });
                if (_cancelRequested) { Done("취소됨", null); return; }
                SetStatus("효과 번역 준비…");
                lib.WarmTranslations();
                if (_cancelRequested) { Done("취소됨", null); return; }

                LogLine("렌더러 생성…");
                var rnd = new SkiaRenderer(lib, _portrait);
                LogLine("렌더러 OK 폰트=" + SkiaRenderer.FontInfo + " " + rnd.W + "x" + rnd.H);
                var sim = new Sim(rep) { Lib = lib };

                string outDir = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath, "videos");
                Directory.CreateDirectory(outDir);
                _outPath = Path.Combine(outDir, "replay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");

                int fps = _fpsVal;
                long ptsStep = 1000000L / fps;
                int frames = 0, events = 0;
                int totalEvents = Math.Max(1, rep.PacketCount);

                LogLine("인코더 생성…");
                using (var enc = new H264Encoder(rnd.W, rnd.H, fps, _outPath))
                {
                    var bmp = new SKBitmap(new SKImageInfo(rnd.W, rnd.H, SKColorType.Bgra8888, SKAlphaType.Premul));
                    using (var canvas = new SKCanvas(bmp))
                    {
                        foreach (ReplayPacket p in rep.Packets)
                        {
                            if (_cancelRequested) break;
                            var before = sim.Snapshot();
                            if (!sim.Apply(p)) continue;
                            sim.Events++; events++;

                            canvas.Clear(new SKColor(0, 0, 0));
                            rnd.Draw(canvas, sim, before, 0.45f);
                            enc.AddFrame(bmp, frames * ptsStep); frames++;
                            canvas.Clear(new SKColor(0, 0, 0));
                            rnd.Draw(canvas, sim, before, 0.75f);
                            enc.AddFrame(bmp, frames * ptsStep); frames++;
                            canvas.Clear(new SKColor(0, 0, 0));
                            rnd.Draw(canvas, sim, before, 1f);
                            enc.AddFrame(bmp, frames * ptsStep); frames++;
                            if (sim.ZoomCode > 0)
                            {
                                canvas.Clear(new SKColor(0, 0, 0));
                                rnd.Draw(canvas, sim, before, 1f);
                                enc.AddFrame(bmp, frames * ptsStep); frames++;
                                canvas.Clear(new SKColor(0, 0, 0));
                                rnd.Draw(canvas, sim, before, 1f);
                                enc.AddFrame(bmp, frames * ptsStep); frames++;
                            }

                            if ((events & 7) == 0)
                            {
                                SetProgress(15 + events * 80 / totalEvents);
                                if (events % 40 == 0) SetStatus(string.Format("렌더 {0}/{1} 이벤트 · {2} 프레임", events, totalEvents, frames));
                            }
                        }
                    }
                    bmp.Dispose();
                    enc.Finish();
                    LogLine(string.Format("인코딩 완료: {0} 프레임", frames));
                }
                rnd.Dispose();

                if (_cancelRequested)
                {
                    try { if (File.Exists(_outPath)) File.Delete(_outPath); } catch { }
                    Done("취소됨", null);
                    return;
                }

                SetProgress(97);
                SetStatus("갤러리에 저장…");
                Android.Net.Uri uri = Publish(_outPath);
                sw.Stop();
                var fi = new FileInfo(_outPath);
                SetProgress(100);
                Done(string.Format("완료 · {0:N1}MB · {1:N0}초", fi.Length / 1048576.0, sw.Elapsed.TotalSeconds), uri);
            }
            catch (Exception ex)
            {
                LogLine("오류: " + ex);
                Done("오류: " + ex.Message, null);
            }
        }

        private void Done(string msg, Android.Net.Uri uri)
        {
            _savedUri = uri;
            RunOnUiThread(delegate
            {
                _busy = false;
                _render.Enabled = true; _pick.Enabled = true; _cancel.Enabled = false;
                _share.Enabled = uri != null || (_outPath != null && File.Exists(_outPath));
                SetStatus(msg);
                LogLine(msg);
            });
        }

        /// <summary>Movies/ReplayVideo 로 복사(API 29+). 실패하면 앱 폴더 경로만 남긴다.</summary>
        private Android.Net.Uri Publish(string path)
        {
            try
            {
                var values = new ContentValues();
                values.Put(MediaStore.Video.Media.InterfaceConsts.DisplayName, Path.GetFileName(path));
                values.Put(MediaStore.Video.Media.InterfaceConsts.MimeType, "video/mp4");
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    values.Put(MediaStore.Video.Media.InterfaceConsts.RelativePath, "Movies/ReplayVideo");
                Android.Net.Uri uri = ContentResolver.Insert(MediaStore.Video.Media.ExternalContentUri, values);
                if (uri == null) return null;
                using (var os = ContentResolver.OpenOutputStream(uri))
                using (var fs = File.OpenRead(path))
                    fs.CopyTo(os);
                LogLine("갤러리 저장: " + uri);
                return uri;
            }
            catch (Exception ex)
            {
                LogLine("갤러리 저장 실패(앱 폴더에 있음): " + ex.Message);
                return null;
            }
        }

        private void OnShare(object sender, EventArgs e)
        {
            try
            {
                Intent intent;
                if (_savedUri != null)
                {
                    intent = new Intent(Intent.ActionView);
                    intent.SetDataAndType(_savedUri, "video/mp4");
                    intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                }
                else if (_outPath != null && File.Exists(_outPath))
                {
                    intent = new Intent(Intent.ActionView);
                    intent.SetDataAndType(Android.Net.Uri.FromFile(new Java.IO.File(_outPath)), "video/mp4");
                    intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                }
                else { SetStatus("공유할 영상이 없습니다"); return; }

                StartActivity(Intent.CreateChooser(intent, "영상 열기"));
            }
            catch (Exception ex) { SetStatus("열기 실패: " + ex.Message); }
        }

        // ── UI 헬퍼(스레드) ───────────────────────────────────
        private void SetStatus(string text)
        {
            RunOnUiThread(delegate { if (_status != null) _status.Text = text; });
        }

        private void SetProgress(int v)
        {
            if (v < 0) v = 0; if (v > 100) v = 100;
            RunOnUiThread(delegate { if (_bar != null) _bar.Progress = v; });
        }

        private void LogLine(string text)
        {
            if (text == null) return;
            Android.Util.Log.Info("ReplayVideo", text);
            RunOnUiThread(delegate
            {
                if (_log == null) return;
                _log.Append(text + "\n");
            });
        }
    }
}
