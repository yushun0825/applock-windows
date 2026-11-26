# アプリロック(Windows)

選んだアプリを、開くときに Windows Hello(PIN・指紋・顔)で確かめる Windows のアプリです。Windows Hello を設定していない PC では、Windows のパスワードで確かめます。

- ロックしたアプリは起動したところで止めておき、確かめるまで窓を出させません(別の窓をかぶせて隠すのではなく、アプリそのものを止めます)
- 確かめられなかったときは、窓を出させないままアプリを終了します(設定で切り替えられます)
- ロックし直すのは「アプリを終了したとき」「一定時間がたったとき」「ほかのアプリに切り替えたとき」から選べます
- 設定の変更とアプリロックの終了にも、確かめることを求められます(設定で切り替えられます)

*English: AppLock for Windows asks for Windows Hello (or your Windows password) before a locked app can open. The app is suspended until you verify, so its windows never appear.*

## 動く Windows

- Windows 11(x64・Arm64)
- 管理者の権限は要りません

## 入れ方

PowerShell を開いて、次の 1 行を貼り付けて Enter を押します。

```powershell
irm https://raw.githubusercontent.com/yushun0825/applock-windows/main/install.ps1 | iex
```

[Releases](https://github.com/yushun0825/applock-windows/releases/latest) から最新の版を取ってきて(SHA-256 で確かめます)、`%LOCALAPPDATA%\Programs\AppLock` に入れ、スタート メニューに「アプリロック」を足して起動します。同じ 1 行で、新しい版に入れ替えられます。

手で入れるときは、[Releases](https://github.com/yushun0825/applock-windows/releases/latest) から `AppLock-win-x64.zip`(Arm の PC は `AppLock-win-arm64.zip`)をダウンロードして展開し、`AppLock.exe` を開きます。実行ファイルに署名をしていないため、ブラウザーでダウンロードしたものを初めて開くときは「Windows によって PC が保護されました」と出ます。「詳細情報」>「実行」で開けます。

## 使い方

1. 「アプリを追加…」で、ロックするアプリを選びます(スタート メニューのアプリ・ストアのアプリ・動いているアプリ。一覧に無いものは「参照…」で実行ファイルを選びます)
2. そのアプリを開くと、本人確認の窓が出ます。確かめると、アプリが開きます

アプリロックは通知領域(タスク バーの右端)で動き続けます。窓を閉じても止まりません。終了するときは、通知領域のアイコンを右クリックして「終了」を選びます。

## できないこと

- 人にのぞかれないためのもので、PC の管理者の権限を持つ人や、タスク マネージャーでアプリロックを終了させる人は止められません
- 管理者として実行しているアプリは、ロックできません
- アプリロックより先に動いていたアプリは、窓を隠しておき、次に前に出すときに確かめます

## 消し方

「設定」>「アプリ」>「インストールされているアプリ」の「アプリロック」で「アンインストール」を選びます。止めていたアプリをすべて動かしてから、アプリロックのファイル・設定・登録を消します。

## プライバシー

アプリロックはネットワークにつなぎません(入れるときと新しい版に入れ替えるときに、GitHub からダウンロードするだけです)。設定と記録はこの PC の `%LOCALAPPDATA%\AppLock` だけに置きます。入力したパスワードは、その場で確かめるためだけに使い、保存しません。

## 自分で組み立てる

[.NET 10 SDK](https://dotnet.microsoft.com/download) が要ります。Windows のほか、Mac や Linux でも組み立てられます。

```bash
dotnet test tests/AppLock.Core.Tests                                    # 単体テスト
dotnet publish src/AppLock/AppLock.csproj -c Release -r win-x64 -o out  # Arm の PC 用は -r win-arm64
```

`-p:AppLockTesting=true` を付けると、確かめるための組み立てになります(偽の認証・別の設定の場所。使い方は `src/AppLock/Platform/Testing.cs`)。`tools/e2e/run.ps1` は、それを Windows で動かして、ロック中にアプリの窓が見えた回数を約 2 ms ごとに数えます。

## 作り

```
src/AppLock.Core/   判断(ロックする・解除する・ロックし直す・設定)。Windows に触らず、単体テストする
src/AppLock/        Windows とのやりとり(プロセスの一時停止・窓・Windows Hello・通知領域)と画面(WPF)
tests/              単体テスト
tools/              アイコンを作る・Windows で動かして確かめる
install.ps1         入れる・新しい版に入れ替える
```

## ライセンス

MIT(`LICENSE`)。配っている実行ファイルには .NET(MIT)が入っています。その表示は `THIRD_PARTY_NOTICES.md` にあります。
