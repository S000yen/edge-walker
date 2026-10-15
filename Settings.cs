namespace EdgeWalker;

/// <summary>調整できる値．画面には依存しない（設定画面・JSONと対応）</summary>
public sealed class Settings
{
    public int CellsShort { get; set; } = 32;          // 画面の短辺に並べるマス数
    public int PlayerMs { get; set; } = 180;           // 主人公の1ブロック時間（ミリ秒）．両モードとも固定で，加速しない
    public double GhostStart { get; set; } = 0.90;     // ゴーストの初速（主人公=1）．両モード共通
    public double GhostRampPerMin { get; set; } = 0.20; // ゴーストが1分ごとに速くなる量（主人公=1との比）
    public double EyeMul { get; set; } = 1.5;          // 目玉の速さ（ゴーストの初速の何倍か）
    public double GhostMax { get; set; } = 1.06;       // ゴーストの最高速（主人公=1との比）
    public int GhostCount { get; set; } = 4;           // 最大4（赤・ピンク・水色・オレンジ）
    public double DotBoost { get; set; } = 0.0;        // 通常クッキーを食べた1ブロックだけ主人公が速くなる割合（0.05=5%）
    public double PowerBoost { get; set; } = 0.0;      // 反撃中に主人公が速くなる割合
    public int PowerSteps { get; set; } = 30;          // 反撃の長さ（ブロック時間）
    public int BlinkSteps { get; set; } = 8;           // 反撃終了前の点滅
    public int Ahead { get; set; } = 7;                // クッキーの出現判定をする先のマス数
    public double PDot { get; set; } = 1.0;            // 通常クッキーの出現確率（先読みのマスには基本的に必ず出る）
    public double PPower { get; set; } = 0.04;         // 大きいクッキーの出現確率（滅多に出ない）
    public int NoCookieHistory { get; set; } = 40;     // 過去このブロック時間以内に通った場所には出さない
    public int SpawnMin { get; set; } = 20;            // ゴーストの初期距離（最小）
    public int SpawnMax { get; set; } = 60;            // ゴーストの初期距離（最大）
    public int SpawnGap { get; set; } = 3;             // ゴースト同士の最小間隔（マス）
    public int FontScalePct { get; set; } = 60;        // 文字の大きさ（%）
    public int BirthStageMs { get; set; } = 120;       // 誕生（11段階）の1段階の長さ（ミリ秒）
    public int BombSteps { get; set; } = 6;            // 食べたゴーストのミニボムを表示するブロック時間
    public double ClickHideMin { get; set; } = 2.0;    // クリックされた後に消える時間（分）
    public double EndHideMin { get; set; } = 0.5;      // 終了後に消える時間（分）
    public bool HideOnFullscreen { get; set; } = true; // 全画面アプリ中は隠す
    public int MemoryLimitPct { get; set; } = 50;      // メモリ使用率がこれ以上なら出現しない（0で無効）
    public string MonitorName { get; set; } = "";       // 表示するモニター（Screen.DeviceName）．空ならプライマリ
    public bool EdgeMode { get; set; } = true;         // エッジモード（画面の縁を歩き回る）を使う．オフなら普段は何も出さない
    public bool IdleMode { get; set; } = true;         // メイズモード（操作がない間，画面いっぱいの迷路で遊ぶ）を使う
    public int IdleSeconds { get; set; } = 60;         // 何秒操作がなければアイドルモードに入るか
    public int IdleRestartSec { get; set; } = 10;      // アイドルモードで1試合終わってから次を始めるまで（秒）
    public bool IdlePersonality { get; set; } = true;  // ゴーストの追いかけ方に個性をつける（オフなら全員が最短で追跡）
    public int IdleVision { get; set; } = 5;           // 迷路で見える範囲（キャラの周囲のマス数）
    public int IdleCookiePct { get; set; } = 100;      // 通路のうちクッキーを置く割合（%）
    public int IdlePower { get; set; } = 10;           // うち大きいクッキーの数
    public int IdleNear { get; set; } = 8;             // 「近い」とみなす距離（マス）．ゴーストが主人公を狙う・主人公が身構える
    public int IdleRevives { get; set; } = 2;          // 主人公が復活できる回数（0〜2．ハートの数）
    public int IdleHouseFirst { get; set; } = 5;       // 最初のゴーストが家を出るまで（ブロック時間）
    public int IdleHouseGap { get; set; } = 10;        // 次のゴーストが出るまでの間隔（ブロック時間）
    public int IdleReviveWait { get; set; } = 6;       // 家で復活してから，また出るまで（ブロック時間）
    public int IdleMistakePct { get; set; } = 2;       // 自動操作のうっかり率（%）
    public int IdleScatterMs { get; set; } = 50;       // 操作されたとき，外周へ逃げる1マスあたりの時間（ミリ秒）
    public bool Autostart { get; set; } = false;       // Windows起動時に自動で常駐

    /// <summary>反撃中のゴーストの速さ：初速の半分</summary>
    public double ScaredRatio => GhostStart * 0.5;

    /// <summary>目玉の速さ：初速の EyeMul 倍</summary>
    public double EyeRatio => GhostStart * EyeMul;

    /// <summary>ゴーストの速さ（主人公=1）．動き出してからの時間（秒）で，初速から少しずつ速くなる</summary>
    public double GhostRatioAt(double paceSec) => Math.Min(GhostMax, GhostStart + GhostRampPerMin * paceSec / 60.0);

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>範囲外の値を丸める．ゴーストの速さは主人公の2倍未満に収める</summary>
    public void Clamp()
    {
        CellsShort = Math.Clamp(CellsShort, 12, 80);
        PlayerMs = Math.Clamp(PlayerMs, 50, 600);
        GhostStart = Math.Clamp(GhostStart, 0.3, 1.9);
        GhostRampPerMin = Math.Clamp(GhostRampPerMin, 0.0, 1.0);
        GhostMax = Math.Clamp(GhostMax, GhostStart, 1.99);
        EyeMul = Math.Clamp(EyeMul, 1.0, 3.0);
        GhostCount = Math.Clamp(GhostCount, 1, 4);
        DotBoost = Math.Clamp(DotBoost, 0.0, 1.0);
        PowerBoost = Math.Clamp(PowerBoost, 0.0, 1.0);
        PowerSteps = Math.Clamp(PowerSteps, 5, 200);
        BlinkSteps = Math.Clamp(BlinkSteps, 0, 40);
        Ahead = Math.Clamp(Ahead, 2, 15);
        PDot = Math.Clamp(PDot, 0.0, 1.0);
        PPower = Math.Clamp(PPower, 0.0, 0.2);
        NoCookieHistory = Math.Clamp(NoCookieHistory, 0, 200);
        SpawnMin = Math.Clamp(SpawnMin, 4, 30);
        SpawnMax = Math.Clamp(SpawnMax, 6, 60);
        if (SpawnMax < SpawnMin) SpawnMax = SpawnMin;
        IdleSeconds = Math.Clamp(IdleSeconds, 5, 3600);
        IdleRestartSec = Math.Clamp(IdleRestartSec, 1, 600);
        IdleVision = Math.Clamp(IdleVision, 3, 12);
        IdleCookiePct = Math.Clamp(IdleCookiePct, 5, 100);
        IdlePower = Math.Clamp(IdlePower, 0, 40);
        IdleNear = Math.Clamp(IdleNear, 3, 30);
        IdleRevives = Math.Clamp(IdleRevives, 0, 2);
        IdleHouseFirst = Math.Clamp(IdleHouseFirst, 1, 100);
        IdleHouseGap = Math.Clamp(IdleHouseGap, 1, 100);
        IdleReviveWait = Math.Clamp(IdleReviveWait, 1, 100);
        IdleMistakePct = Math.Clamp(IdleMistakePct, 0, 50);
        IdleScatterMs = Math.Clamp(IdleScatterMs, 15, 300);
        SpawnGap = Math.Clamp(SpawnGap, 1, 8);
        FontScalePct = Math.Clamp(FontScalePct, 30, 200);
        BirthStageMs = Math.Clamp(BirthStageMs, 30, 600);
        BombSteps = Math.Clamp(BombSteps, 1, 40);
        ClickHideMin = Math.Clamp(ClickHideMin, 0.1, 60.0);
        EndHideMin = Math.Clamp(EndHideMin, 0.1, 120.0);
        MemoryLimitPct = Math.Clamp(MemoryLimitPct, 0, 100);
    }
}
