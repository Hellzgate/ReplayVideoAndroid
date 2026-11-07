# ReplayVideoAndroid

**EDOPro 리플레이(`.yrpX`) → 보드 영상(MP4)** 안드로이드 앱. (SkiaSharp 렌더 + MediaCodec 인코딩, ffmpeg 불필요)

> 렌더 상태머신은 윈도우 앱과 **같은 파일**(`..\_shared\ReplaySim.cs`)을 링크 컴파일합니다.

## 기능

| 구분 | 내용 |
|---|---|
| 입력 | 리플레이 파일 선택(SAF) · `testReplay` adb 훅 |
| 옵션 | fps(8/12/15/24) · 세로 1080×1920 / 가로 1400×820 |
| 출력 | `Movies/ReplayVideo` 에 저장(MediaStore) · 공유/열기 |
| 카드 정보 | **cdb(레포) → API** 순서 · 효과 한글 번역 · 이미지 캐시 |
| **카드 DB 레포** | EDOPro 와 같은 레포에서 cdb 를 **zip 으로 받아** 사용(앱 전용 저장소) |
| 기타 | 진행률·로그·취소 · 다크 테마 |

## 카드 DB 레포 (v1.1)

EDOPro 의 `configs.json` + `user_configs.json` 에 있던 레포를 기본 목록으로 넣었습니다.

- **[카드 DB (레포)]** → 목록(체크로 사용/제외) → **[받기 / 갱신]**
- 받은 위치: `Android/data/com.hellzgate.replayvideo/files/cdb/repos/<owner>_<repo>`
- git 이 없으므로 `codeload.github.com/.../zip` 으로 받습니다(앱 내장 `System.IO.Compression`)
- 카드 조회 순서:
  1. cdb 이름이 **한국어**면 그대로
  2. YGOPRODeck API 한국어
  3. cdb 이름(영문 등 — **API 에 없는 커스텀 카드**가 여기서 해결)
  4. API 영문 → `#ID`
- 효과 텍스트도 같은 순서(없으면 Google 번역)
- cdb 는 SQLite 이므로 `Microsoft.Data.Sqlite` + `libe_sqlite3.so`(arm64/arm) 포함 → APK **약 +2.8MB**

기본 레포: `Team-AllYGOPro/edopro-korean` · `edopro-korean-delta` · `Hellzgate/EdoKroCDB` · `CCC-Server/EMP-TY` · `ProjectIgnis/DeltaBagooska`

## 빌드 (Windows)

```powershell
# 안드로이드 워크로드가 있는 사용자 로컬 dotnet 사용
$dotnet = "$env:LOCALAPPDATA\dotnet-android\dotnet.exe"
$sdk    = "$env:LOCALAPPDATA\Android\Sdk"

dotnet build -c Release "-p:AndroidSdkDirectory=$sdk"
# 산출물: bin\Release\net8.0-android\com.hellzgate.replayvideo-Signed.apk
```

> 시스템 dotnet 에는 android 워크로드가 없습니다(실측). `dotnet workload install android` 로 설치하거나 위 경로를 쓰세요.

## 검증 훅 (adb)

```bash
# 렌더
adb shell am start -n com.hellzgate.replayvideo/.MainActivity --es testReplay "test.yrpX" --ei fps 8
# 카드 DB 레포 받기
adb shell am start -n com.hellzgate.replayvideo/.MainActivity --es cdbSync "Hellzgate/EdoKroCDB"
# cdb 조회 확인
adb shell am start -n com.hellzgate.replayvideo/.MainActivity --es cdbProbe "10000010,89631139"
adb logcat -s ReplayVideo
```

> 화면이 꺼져 있으면 `adb shell input keyevent KEYCODE_WAKEUP` 먼저.

## 빌드 준비 — 형제 폴더

`ReplayVideoAndroid` · `_shared`(ReplaySim.cs) · `WindBotAIEditor`(ReplayParser.cs) 가 같은 상위 폴더에 있어야 합니다.
(배포 백업 ZIP 에는 필요한 파일이 포함되어 있습니다.)

## 한계

- 리플레이에 **선택/응답이 기록되지 않아** 화면에는 캡션으로만 표시됩니다.
- 원본 이펙트·연출은 재현하지 않습니다(2D 보드 애니메이션).
- 사설/비공개 레포는 토큰이 필요해 받을 수 없습니다.
