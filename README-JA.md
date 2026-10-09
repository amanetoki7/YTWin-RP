# YTWin-RP
([English](README.md))

**ブラウザーで再生している YouTube / YouTube Music** を Discord のリッチプレゼンスに表示する Windows 用クライアントです。  
動画は「〜を視聴中」、音楽は「〜を再生中」（曲名・アーティスト・アルバムアート付き）として表示し、音楽は Last.FM / ListenBrainz へのスクロブルもできます。

YTWin-RP は [AMWin-RP](https://github.com/PKBeam/AMWin-RP)（Apple Music 用）のフォークを YouTube 向けに作り直したものです。

## 仕組み（ブラウザー拡張機能は不要）

1. ブラウザーは再生中のメディア情報を Windows のメディアコントロール（音量オーバーレイに出るもの）に渡しています。YTWin-RP はそこからタイトル・チャンネル名・再生/一時停止・再生位置を読み取ります。
2. それが YouTube であることを、ブラウザーのウィンドウタイトル（`動画タイトル - YouTube - Google Chrome`）で確認します。バックグラウンドのタブの場合は、タイトルとチャンネル名で YouTube を検索して確認します。この検索で動画 ID も分かるので、サムネイルと「YouTube で見る」ボタンが付きます。
3. **動画か音楽か？** 同じ動画 ID を YouTube Music に問い合わせます。YouTube Music はカタログ内の動画に種別を付けており（`MUSIC_VIDEO_TYPE_ATV` = 自動生成の曲、`OMV` = 公式ミュージックビデオ、`UGC` = 音楽カテゴリの一般投稿）、カタログ外の動画には種別が付きません。種別が付いていれば音楽、なければ動画として扱います。音楽の場合は曲名・アーティスト・アルバム名・正方形のアルバムアートも YouTube Music から取得します。

Chrome / Edge / Firefox / Brave / Vivaldi / Opera と、インストールした YouTube PWA を認識します。それ以外のアプリ（Spotify デスクトップ版など）のメディア情報は無視します。

### 任意：ブラウザー拡張機能

[`extension`](extension/) フォルダーの拡張機能（パッケージ化されていない拡張機能として読み込み）は、正確な動画 ID と再生位置を `127.0.0.1` 経由でアプリに送ります。必要なのは、限定公開の動画、検索で特定できないバックグラウンドのタブ、より正確なタイムスタンプが欲しい場合だけです。詳細は [extension/README.md](extension/README.md) を参照してください。

## インストール
YTWin-RP には **Windows 11 24H2 以降** と Discord のデスクトップクライアントが必要です。

ビルド済みファイルは [こちら](https://github.com/amanetoki7/YTWin-RP/releases)。x64 / ARM64 を選び、`NoRuntime` 版を使う場合は [.NET 10 デスクトップランタイム](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0) が必要です。

### Discord アプリケーション

Discord は「〜を視聴中」「〜を再生中」の「〜」部分に、プレゼンスを設定した**アプリケーションの名前**（YouTube / YouTube Music）を表示します。両方のアプリケーションは YTWin-RP に組み込まれているため、利用者側の設定は不要です。サムネイルは URL で送り、その右下の小アイコンには Developer Portal で設定したアプリケーションのアイコンを使います（`youtube` / `youtubemusic` / `pause` という名前の Rich Presence アセットをアップロードするとそちらが優先されます）。組み込み ID のないビルドでは、代わりに設定画面に Application ID の入力欄が表示されます。

## 使い方
- `.exe` を起動するとシステムトレイに常駐します。アイコンをダブルクリックで設定、右クリック → 終了で終了します。
- ブラウザーで youtube.com または music.youtube.com を再生すると、数秒後にリッチプレゼンスが表示されます（既定では再生中のみ。「一時停止中でも表示」は設定で変更できます）。
- 設定の「検出」ページで、今なにをどう検出しているかを確認できます。

知っておくと便利な設定：
- **YouTube Music を使って音楽を判定** – オフにするとすべて動画として表示します。
- **YouTube のタブが前面にあるときだけ追跡する** – 同じブラウザーで他の Web プレイヤー（Spotify Web など）も使う人向けの厳格モード。
- **動画／音楽のリッチプレゼンスを表示** – どちらかだけ表示することもできます。

## スクロブル
既定では音楽と判定されたものだけをスクロブルします（すべてスクロブルするオプションあり）。曲名・アーティスト・アルバムは YouTube Music の情報を使い、取得できない場合は動画タイトルを整形します（`アーティスト - 曲名 (Official Video)` → *アーティスト* / *曲名*）。

オフラインでのスクロブルには対応していません。

### Last.FM
https://www.last.fm/api の「Get an API Account」で API Key と API Secret を取得し、Last.FM のユーザー名・パスワードとともに設定に入力してください。パスワードは Windows 資格情報マネージャーに保存されます。

### ListenBrainz
設定にユーザートークンを入力してください。

## ビルド
```
dotnet build YTWin-RichPresence.sln -c Release
```
配布用ビルドを作る前に、Developer Portal で `YouTube`（任意で `YouTube Music` も）という名前のアプリケーションを作成し、その Application ID を `YTWin-RichPresence/Constants.cs` の `DefaultDiscordClientID` / `DefaultDiscordClientIDMusic` に入れてください。これが空のままだと、利用者が自分で ID を入力する必要があります。

`Properties/Localisation.resx` を dotnet CLI だけで編集した場合は `GenerateLocalisation.ps1` を実行して Designer ファイルを再生成してください（Visual Studio なら自動で行われます）。

## バグ報告
`%localappdata%\YTWin-RichPresence` にある `.log` ファイルを添付してください。どのメディアセッションを見つけ、どう動画を特定し、Discord に何を送ったかが記録されています。

投稿前に、Discord の設定でアクティビティの共有が有効になっているか（設定 > アクティビティのプライバシー > アクティビティステータスを共有）を確認してください。
