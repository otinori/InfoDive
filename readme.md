# InfoDive

インフォグラフィック画像を題材に、パン／ズームのシナリオを組み立ててプレゼンテーションとして再生する Windows デスクトップアプリ。

**License**: Apache 2.0

---

## 概要

### 開発動機

ChatGPTやClaude、NoteboolLMなどが描くプレゼン資料はかなり良いものが生成される。それならば、いっそのこと、PowerPointに貼り付けてプレゼンするのではなく、画像をそのままストーリーに仕立ててプレゼン資料として使えないか？と考えた。
高解像度のインフォグラフィックや図解を「拡大縮小させながら説明する」シナリオをサクッとつくてプレゼンするという、シナリオ作成と再生機能を作ってみたいと思ったことがきっかけとなった。

### ゴール

大判の画像（インフォグラフィック・設計図・ホワイトボード写真など）をそのまま使い、パン／ズームのシナリオを組み立ててプレゼンできる環境を作る。

### 何ができるのか

- 高解像度画像（最大 8192×8192px）をズームして高精細に表示
- ドラッグ＆ドロップでシーン（キーフレーム）を構築
- プレゼン中にフリーハンド描画（アノテーション）
- PDF・draw.io（`.drawio` / `.dio`）の取り込み
- プレゼンシナリオを `.przip` 形式（画像＋シナリオを ZIP 圧縮）で一括管理
- `.przip` のダブルクリックで即時プレゼン開始

---

## 動作環境

### 前提条件

- .NET 9.0（自己完結型ビルドの場合は不要）

### 対応プラットフォーム

Windows 10 (21H2 以降) / Windows 11

※そのうちMac版も作成するかも

---

## クイックスタート

### デバッグビルドして起動

```bash
dotnet build src/apps/InfoDive/InfoDive.csproj
```

### リリースビルド（単一ファイル・自己完結型）

```bash
dotnet publish src/apps/InfoDive/InfoDive.csproj -c Release
```

### MSI インストーラーを作る

```powershell
# 初回のみ: WiX v5 グローバルツールをインストール
dotnet tool install --global wix --version 5.*
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2

# publish → MSI 生成（出力: dist/InfoDive-<version>-win-x64.msi）
pwsh tools/build.ps1
```

---

## 主な使い方

1. **画像を開く** — ウィンドウ中央のドロップゾーンへ画像をドラッグ＆ドロップ
2. **ビューを調整** — ドラッグでパン、ホイールでズーム
3. **シーンを登録** — サイドバーの「＋ 現在のビューをシーンに追加」でキーフレーム化
4. **プレゼン開始** — `Ctrl+Enter` または「▶ プレゼン開始」ボタン

対応形式: JPG / PNG / BMP / GIF / WebP / SVG / PDF / draw.io

詳しい操作方法・キーボードショートカットは [`docs/manual/user-guide/usage.md`](docs/manual/user-guide/usage.md) を参照。

---

## 構成

```
InfoDive/
├── src/
│   ├── apps/InfoDive/            # WPF アプリ本体
│   │   ├── Models/               # データモデル（Keyframe / LoadedImage 等）
│   │   ├── Services/             # ファイル読み書き・Undo・SVG/PDF/draw.io 変換
│   │   └── Converters/           # XAML バインディング用コンバーター
│   └── installer/wix/            # WiX インストーラー定義
├── tools/
│   └── build.ps1                 # publish → MSI 生成スクリプト
├── docs/
│   ├── design/spec/              # 仕様書
│   └── manual/user-guide/        # 利用者向けドキュメント
└── tasks/
    └── TASKS.md                  # 課題管理
```

**技術スタック**: .NET 9 / WPF / C# / SharpVectors.Reloaded（SVG）/ PDFtoImage（PDF）/ WiX v5（MSI）

---

## ライセンス

Apache License 2.0 — [LICENSE](LICENSE) を参照。
