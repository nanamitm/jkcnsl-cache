namespace jkcnsl_cache;

public sealed record JkServiceMapEntry(
    ServiceKey ServiceKey,
    string JkId,
    bool IsPrimary = true,
    string? Notes = null);

public static class NetworkServiceIdTable
{
    // NicoJK の ntsID テーブルをそのまま移植するのではなく、
    // 実 ONID/TSID/SID ベースの ServiceKey を正規化した対応表として持つ。
    // BS/高度BS は ChSet4 と NicoJK の既存対応を突き合わせて補完する。
    // 地上波は現時点の調査範囲では ONID == TSID で扱えるため、
    // エントリ定義ではその前提をヘルパーに閉じ込めて記述する。
    // 放送大学の地上波放送は終了済みのため対象外。
    public static readonly IReadOnlyList<JkServiceMapEntry> All = new JkServiceMapEntry[]
    {
        // BS
        SatellitePrimary(0x0004, 16625, 101, "jk101", "ＮＨＫ ＢＳ"),
        Satellite(0x0004, 16625, 102, "jk102", "ＮＨＫ ＢＳサブ", isPrimary: false),
        SatellitePrimary(0x0004, 16592, 141, "jk141", "ＢＳ日テレ"),
        Satellite(0x0004, 16592, 142, "jk142", "ＢＳ日テレサブ", isPrimary: false),
        Satellite(0x0004, 16592, 143, "jk143", "ＢＳ日テレサブ", isPrimary: false),
        SatellitePrimary(0x0004, 16400, 151, "jk151", "ＢＳ朝日"),
        SatellitePrimary(0x0004, 16401, 161, "jk161", "ＢＳ－ＴＢＳ"),
        Satellite(0x0004, 16401, 162, "jk162", "ＢＳ－ＴＢＳサブ", isPrimary: false),
        Satellite(0x0004, 16401, 163, "jk163", "ＢＳ－ＴＢＳサブ", isPrimary: false),
        SatellitePrimary(0x0004, 16402, 171, "jk171", "ＢＳテレ東"),
        SatellitePrimary(0x0004, 16593, 181, "jk181", "ＢＳフジ"),
        Satellite(0x0004, 16593, 182, "jk182", "ＢＳフジサブ", isPrimary: false),
        Satellite(0x0004, 16593, 183, "jk183", "ＢＳフジサブ", isPrimary: false),
        SatellitePrimary(0x0004, 16432, 191, "jk191", "ＷＯＷＯＷプライム"),
        SatellitePrimary(0x0004, 17488, 192, "jk192", "ＷＯＷＯＷライブ"),
        SatellitePrimary(0x0004, 17489, 193, "jk193", "ＷＯＷＯＷシネマ"),
        SatellitePrimary(0x0004, 18675, 200, "jk200", "ＢＳ１０"),
        SatellitePrimary(0x0004, 16626, 201, "jk201", "ＢＳ１０スターチャンネル"),
        SatellitePrimary(0x0004, 16528, 211, "jk211", "ＢＳ１１"),
        SatellitePrimary(0x0004, 16530, 222, "jk222", "ＢＳ１２"),
        SatellitePrimary(0x0004, 18130, 231, "jk231", "放送大学テレビ"),
        Satellite(0x0004, 18130, 232, "jk232", "放送大学テレビサブ", isPrimary: false),
        SatellitePrimary(0x0004, 18258, 234, "jk234", "グリーンチャンネル"),
        SatellitePrimary(0x0004, 17969, 236, "jk236", "ＢＳアニマックス"),
        SatellitePrimary(0x0004, 18225, 242, "jk242", "Ｊ　ＳＰＯＲＴＳ　１"),
        SatellitePrimary(0x0004, 18226, 243, "jk243", "Ｊ　ＳＰＯＲＴＳ　２"),
        SatellitePrimary(0x0004, 18227, 244, "jk244", "Ｊ　ＳＰＯＲＴＳ　３"),
        SatellitePrimary(0x0004, 18224, 245, "jk245", "Ｊ　ＳＰＯＲＴＳ　４"),
        SatellitePrimary(0x0004, 17970, 251, "jk251", "ＢＳ釣りビジョン"),
        SatellitePrimary(0x0004, 18256, 252, "jk252", "ＷＯＷＯＷプラス"),
        SatellitePrimary(0x0004, 18257, 255, "jk255", "日本映画専門ｃｈ"),
        SatellitePrimary(0x0004, 18288, 256, "jk256", "ディズニーｃｈ"),
        SatellitePrimary(0x0004, 18803, 260, "jk260", "Ｊ：ＣＯＭ　ＢＳ"),
        SatellitePrimary(0x0004, 18801, 265, "jk265", "ＢＳよしもと"),
        SatellitePrimary(0x0004, 18130, 531, "jk531", "放送大学ラジオ"),

        // 高度BS
        SatellitePrimary(0x000B, 45328, 101, "jk103", "ＮＨＫ　ＢＳＰ４Ｋ"),
        SatellitePrimary(0x000B, 45280, 102, "jk104", "ＮＨＫ　ＢＳ８Ｋ"),

        // CS
        SatellitePrimary(0x0007, 28928, 333, "jk333", "ＡＴ－Ｘ"),

        // 関東広域
        TerrestrialPrimary(0x7FE0, 0x0400, "jk1", "ＮＨＫ総合・東京"),
        TerrestrialPrimary(0x7FE1, 0x0408, "jk2", "ＮＨＫＥテレ東京"),
        TerrestrialPrimary(0x7FE2, 0x0410, "jk4", "日本テレビ"),
        TerrestrialPrimary(0x7FE3, 0x0418, "jk6", "ＴＢＳ"),
        TerrestrialPrimary(0x7FE4, 0x0420, "jk8", "フジテレビジョン"),
        TerrestrialPrimary(0x7FE5, 0x0428, "jk5", "テレビ朝日"),
        TerrestrialPrimary(0x7FE6, 0x0430, "jk7", "テレビ東京"),

        // 近畿広域
        TerrestrialPrimary(0x7FD1, 0x0808, "jk2", "ＮＨＫＥテレ大阪"),
        TerrestrialPrimary(0x7FD2, 0x0810, "jk6", "ＭＢＳ毎日放送"),
        TerrestrialPrimary(0x7FD3, 0x0818, "jk5", "ＡＢＣテレビ"),
        TerrestrialPrimary(0x7FD4, 0x0820, "jk8", "関西テレビ"),
        TerrestrialPrimary(0x7FD5, 0x0828, "jk4", "読売テレビ"),

        // 中京広域
        TerrestrialPrimary(0x7FC1, 0x0C08, "jk2", "ＮＨＫＥテレ名古屋"),
        TerrestrialPrimary(0x7FC2, 0x0C10, "jk8", "東海テレビ"),
        TerrestrialPrimary(0x7FC3, 0x0C18, "jk6", "ＣＢＣ"),
        TerrestrialPrimary(0x7FC4, 0x0C20, "jk5", "メ～テレ"),
        TerrestrialPrimary(0x7FC5, 0x0C28, "jk4", "中京テレビ"),

        // 北海道域
        TerrestrialPrimary(0x7FB2, 0x1010, "jk6", "ＨＢＣ北海道放送"),
        TerrestrialPrimary(0x7FB3, 0x1018, "jk4", "ＳＴＶ札幌テレビ"),
        TerrestrialPrimary(0x7FB4, 0x1020, "jk5", "ＨＴＢ北海道テレビ"),
        TerrestrialPrimary(0x7FB5, 0x1028, "jk8", "ＵＨＢ"),
        TerrestrialPrimary(0x7FB6, 0x1030, "jk7", "ＴＶＨ"),

        // 岡山香川
        TerrestrialPrimary(0x7FA2, 0x1410, "jk4", "ＲＮＣ西日本テレビ"),
        TerrestrialPrimary(0x7FA3, 0x1418, "jk5", "ＫＳＢ瀬戸内海放送"),
        TerrestrialPrimary(0x7FA4, 0x1420, "jk6", "ＲＳＫテレビ"),
        TerrestrialPrimary(0x7FA5, 0x1428, "jk7", "ＴＳＣテレビせとうち"),
        TerrestrialPrimary(0x7FA6, 0x1430, "jk8", "ＯＨＫテレビ"),

        // 島根鳥取
        TerrestrialPrimary(0x7F92, 0x1810, "jk8", "山陰中央テレビ"),
        TerrestrialPrimary(0x7F93, 0x1818, "jk6", "ＢＳＳテレビ"),
        TerrestrialPrimary(0x7F94, 0x1820, "jk4", "日本海テレビ"),

        // 北海道（札幌）
        TerrestrialPrimary(0x7F50, 0x2800, "jk1", "ＮＨＫ総合・札幌"),
        TerrestrialPrimary(0x7F51, 0x2808, "jk2", "ＮＨＫＥテレ札幌"),
        TerrestrialPrimary(0x7F52, 0x2810, "jk6", "ＨＢＣ札幌"),
        TerrestrialPrimary(0x7F53, 0x2818, "jk4", "ＳＴＶ札幌"),
        TerrestrialPrimary(0x7F54, 0x2820, "jk5", "ＨＴＢ札幌"),
        TerrestrialPrimary(0x7F55, 0x2828, "jk8", "ＵＨＢ札幌"),
        TerrestrialPrimary(0x7F56, 0x2830, "jk7", "ＴＶＨ札幌"),

        // 北海道（函館）
        TerrestrialPrimary(0x7F40, 0x2C00, "jk1", "ＮＨＫ総合・函館"),
        TerrestrialPrimary(0x7F41, 0x2C08, "jk2", "ＮＨＫＥテレ函館"),
        TerrestrialPrimary(0x7F42, 0x2C10, "jk6", "ＨＢＣ函館"),
        TerrestrialPrimary(0x7F43, 0x2C18, "jk4", "ＳＴＶ函館"),
        TerrestrialPrimary(0x7F44, 0x2C20, "jk5", "ＨＴＢ函館"),
        TerrestrialPrimary(0x7F45, 0x2C28, "jk8", "ＵＨＢ函館"),
        TerrestrialPrimary(0x7F46, 0x2C30, "jk7", "ＴＶＨ函館"),

        // 北海道（旭川）
        TerrestrialPrimary(0x7F30, 0x3000, "jk1", "ＮＨＫ総合・旭川"),
        TerrestrialPrimary(0x7F31, 0x3008, "jk2", "ＮＨＫＥテレ旭川"),
        TerrestrialPrimary(0x7F32, 0x3010, "jk6", "ＨＢＣ旭川"),
        TerrestrialPrimary(0x7F33, 0x3018, "jk4", "ＳＴＶ旭川"),
        TerrestrialPrimary(0x7F34, 0x3020, "jk5", "ＨＴＢ旭川"),
        TerrestrialPrimary(0x7F35, 0x3028, "jk8", "ＵＨＢ旭川"),
        TerrestrialPrimary(0x7F36, 0x3030, "jk7", "ＴＶＨ旭川"),

        // 北海道（帯広）
        TerrestrialPrimary(0x7F20, 0x3400, "jk1", "ＮＨＫ総合・帯広"),
        TerrestrialPrimary(0x7F21, 0x3408, "jk2", "ＮＨＫＥテレ帯広"),
        TerrestrialPrimary(0x7F22, 0x3410, "jk6", "ＨＢＣ帯広"),
        TerrestrialPrimary(0x7F23, 0x3418, "jk4", "ＳＴＶ帯広"),
        TerrestrialPrimary(0x7F24, 0x3420, "jk5", "ＨＴＢ帯広"),
        TerrestrialPrimary(0x7F25, 0x3428, "jk8", "ＵＨＢ帯広"),
        TerrestrialPrimary(0x7F26, 0x3430, "jk7", "ＴＶＨ帯広"),

        // 北海道（釧路）
        TerrestrialPrimary(0x7F10, 0x3800, "jk1", "ＮＨＫ総合・釧路"),
        TerrestrialPrimary(0x7F11, 0x3808, "jk2", "ＮＨＫＥテレ釧路"),
        TerrestrialPrimary(0x7F12, 0x3810, "jk6", "ＨＢＣ釧路"),
        TerrestrialPrimary(0x7F13, 0x3818, "jk4", "ＳＴＶ釧路"),
        TerrestrialPrimary(0x7F14, 0x3820, "jk5", "ＨＴＢ釧路"),
        TerrestrialPrimary(0x7F15, 0x3828, "jk8", "ＵＨＢ釧路"),
        TerrestrialPrimary(0x7F16, 0x3830, "jk7", "ＴＶＨ釧路"),

        // 北海道（北見）
        TerrestrialPrimary(0x7F00, 0x3C00, "jk1", "ＮＨＫ総合・北見"),
        TerrestrialPrimary(0x7F01, 0x3C08, "jk2", "ＮＨＫＥテレ北見"),
        TerrestrialPrimary(0x7F02, 0x3C10, "jk6", "ＨＢＣ北見"),
        TerrestrialPrimary(0x7F03, 0x3C18, "jk4", "ＳＴＶ北見"),
        TerrestrialPrimary(0x7F04, 0x3C20, "jk5", "ＨＴＢ北見"),
        TerrestrialPrimary(0x7F05, 0x3C28, "jk8", "ＵＨＢ北見"),
        TerrestrialPrimary(0x7F06, 0x3C30, "jk7", "ＴＶＨ北見"),

        // 北海道（室蘭）
        TerrestrialPrimary(0x7EF0, 0x4000, "jk1", "ＮＨＫ総合・室蘭"),
        TerrestrialPrimary(0x7EF1, 0x4008, "jk2", "ＮＨＫＥテレ室蘭"),
        TerrestrialPrimary(0x7EF2, 0x4010, "jk6", "ＨＢＣ室蘭"),
        TerrestrialPrimary(0x7EF3, 0x4018, "jk4", "ＳＴＶ室蘭"),
        TerrestrialPrimary(0x7EF4, 0x4020, "jk5", "ＨＴＢ室蘭"),
        TerrestrialPrimary(0x7EF5, 0x4028, "jk8", "ＵＨＢ室蘭"),
        TerrestrialPrimary(0x7EF6, 0x4030, "jk7", "ＴＶＨ室蘭"),

        // 宮城
        TerrestrialPrimary(0x7EE0, 0x4400, "jk1", "ＮＨＫ総合・仙台"),
        TerrestrialPrimary(0x7EE1, 0x4408, "jk2", "ＮＨＫＥテレ仙台"),
        TerrestrialPrimary(0x7EE2, 0x4410, "jk6", "ＴＢＣテレビ"),
        TerrestrialPrimary(0x7EE3, 0x4418, "jk8", "仙台放送"),
        TerrestrialPrimary(0x7EE4, 0x4420, "jk4", "ミヤギテレビ"),
        TerrestrialPrimary(0x7EE5, 0x4428, "jk5", "ＫＨＢ東日本放送"),

        // 秋田
        TerrestrialPrimary(0x7ED0, 0x4800, "jk1", "ＮＨＫ総合・秋田"),
        TerrestrialPrimary(0x7ED1, 0x4808, "jk2", "ＮＨＫＥテレ秋田"),
        TerrestrialPrimary(0x7ED2, 0x4810, "jk4", "ＡＢＳ秋田放送"),
        TerrestrialPrimary(0x7ED3, 0x4818, "jk8", "ＡＫＴ秋田テレビ"),
        TerrestrialPrimary(0x7ED4, 0x4820, "jk5", "ＡＡＢ秋田朝日放送"),

        // 山形
        TerrestrialPrimary(0x7EC0, 0x4C00, "jk1", "ＮＨＫ総合・山形"),
        TerrestrialPrimary(0x7EC1, 0x4C08, "jk2", "ＮＨＫＥテレ山形"),
        TerrestrialPrimary(0x7EC2, 0x4C10, "jk4", "ＹＢＣ山形放送"),
        TerrestrialPrimary(0x7EC3, 0x4C18, "jk5", "ＹＴＳ山形テレビ"),
        TerrestrialPrimary(0x7EC4, 0x4C20, "jk6", "テレビユー山形"),
        TerrestrialPrimary(0x7EC5, 0x4C28, "jk8", "さくらんぼテレビ"),

        // 岩手
        TerrestrialPrimary(0x7EB0, 0x5000, "jk1", "ＮＨＫ総合・盛岡"),
        TerrestrialPrimary(0x7EB1, 0x5008, "jk2", "ＮＨＫＥテレ盛岡"),
        TerrestrialPrimary(0x7EB2, 0x5010, "jk6", "ＩＢＣテレビ"),
        TerrestrialPrimary(0x7EB3, 0x5018, "jk4", "テレビ岩手"),
        TerrestrialPrimary(0x7EB4, 0x5020, "jk8", "めんこいテレビ"),
        TerrestrialPrimary(0x7EB5, 0x5028, "jk5", "岩手朝日テレビ"),

        // 福島
        TerrestrialPrimary(0x7EA0, 0x5400, "jk1", "ＮＨＫ総合・福島"),
        TerrestrialPrimary(0x7EA1, 0x5408, "jk2", "ＮＨＫＥテレ福島"),
        TerrestrialPrimary(0x7EA2, 0x5410, "jk8", "福島テレビ"),
        TerrestrialPrimary(0x7EA3, 0x5418, "jk4", "福島中央テレビ"),
        TerrestrialPrimary(0x7EA4, 0x5420, "jk5", "ＫＦＢ福島放送"),
        TerrestrialPrimary(0x7EA5, 0x5428, "jk6", "テレビユー福島"),

        // 青森
        TerrestrialPrimary(0x7E90, 0x5800, "jk1", "ＮＨＫ総合・青森"),
        TerrestrialPrimary(0x7E91, 0x5808, "jk2", "ＮＨＫＥテレ青森"),
        TerrestrialPrimary(0x7E92, 0x5810, "jk4", "ＲＡＢ青森放送"),
        TerrestrialPrimary(0x7E93, 0x5818, "jk6", "ＡＴＶ青森テレビ"),
        TerrestrialPrimary(0x7E94, 0x5820, "jk5", "青森朝日放送"),

        // 東京
        TerrestrialPrimary(0x7E87, 0x5C38, "jk9", "ＴＯＫＹＯ ＭＸ"),

        // 神奈川
        TerrestrialPrimary(0x7E77, 0x6038, "jk11", "ｔｖｋ"),

        // 群馬
        TerrestrialPrimary(0x7E60, 0x6400, "jk1", "ＮＨＫ総合・前橋"),
        TerrestrialPrimary(0x7E67, 0x6438, "jk15", "群馬テレビ"),

        // 茨城
        TerrestrialPrimary(0x7E50, 0x6800, "jk1", "ＮＨＫ総合・水戸"),

        // 千葉
        TerrestrialPrimary(0x7E47, 0x6C38, "jk12", "チバテレビ"),

        // 栃木
        TerrestrialPrimary(0x7E30, 0x7000, "jk1", "ＮＨＫ総合・宇都宮"),
        TerrestrialPrimary(0x7E37, 0x7038, "jk16", "とちぎテレビ"),

        // 埼玉
        TerrestrialPrimary(0x7E27, 0x7438, "jk10", "テレ玉"),

        // 長野
        TerrestrialPrimary(0x7E10, 0x7800, "jk1", "ＮＨＫ総合・長野"),
        TerrestrialPrimary(0x7E11, 0x7808, "jk2", "ＮＨＫＥテレ長野"),
        TerrestrialPrimary(0x7E12, 0x7810, "jk4", "テレビ信州"),
        TerrestrialPrimary(0x7E13, 0x7818, "jk5", "ａｂｎ長野朝日放送"),
        TerrestrialPrimary(0x7E14, 0x7820, "jk6", "ＳＢＣ信越放送"),
        TerrestrialPrimary(0x7E15, 0x7828, "jk8", "ＮＢＳ長野放送"),

        // 新潟
        TerrestrialPrimary(0x7E00, 0x7C00, "jk1", "ＮＨＫ総合・新潟"),
        TerrestrialPrimary(0x7E01, 0x7C08, "jk2", "ＮＨＫＥテレ新潟"),
        TerrestrialPrimary(0x7E02, 0x7C10, "jk6", "ＢＳＮ"),
        TerrestrialPrimary(0x7E03, 0x7C18, "jk8", "ＮＳＴ"),
        TerrestrialPrimary(0x7E04, 0x7C20, "jk4", "ＴｅＮＹテレビ新潟"),
        TerrestrialPrimary(0x7E05, 0x7C28, "jk5", "新潟テレビ２１"),

        // 山梨
        TerrestrialPrimary(0x7DF0, 0x8000, "jk1", "ＮＨＫ総合・甲府"),
        TerrestrialPrimary(0x7DF1, 0x8008, "jk2", "ＮＨＫＥテレ甲府"),
        TerrestrialPrimary(0x7DF2, 0x8010, "jk4", "ＹＢＳ山梨放送"),
        TerrestrialPrimary(0x7DF3, 0x8018, "jk6", "ＵＴＹ"),

        // 愛知
        TerrestrialPrimary(0x7DE0, 0x8400, "jk1", "ＮＨＫ総合・名古屋"),
        TerrestrialPrimary(0x7DE6, 0x8430, "jk7", "テレビ愛知"),

        // 石川
        TerrestrialPrimary(0x7DD0, 0x8800, "jk1", "ＮＨＫ総合・金沢"),
        TerrestrialPrimary(0x7DD1, 0x8808, "jk2", "ＮＨＫＥテレ金沢"),
        TerrestrialPrimary(0x7DD2, 0x8810, "jk4", "テレビ金沢"),
        TerrestrialPrimary(0x7DD3, 0x8818, "jk5", "北陸朝日放送"),
        TerrestrialPrimary(0x7DD4, 0x8820, "jk6", "ＭＲＯ"),
        TerrestrialPrimary(0x7DD5, 0x8828, "jk8", "石川テレビ"),

        // 静岡
        TerrestrialPrimary(0x7DC0, 0x8C00, "jk1", "ＮＨＫ総合・静岡"),
        TerrestrialPrimary(0x7DC1, 0x8C08, "jk2", "ＮＨＫＥテレ静岡"),
        TerrestrialPrimary(0x7DC2, 0x8C10, "jk6", "ＳＢＳ"),
        TerrestrialPrimary(0x7DC3, 0x8C18, "jk8", "テレビ静岡"),
        TerrestrialPrimary(0x7DC4, 0x8C20, "jk4", "だいいちテレビ"),
        TerrestrialPrimary(0x7DC5, 0x8C28, "jk5", "静岡朝日テレビ"),

        // 福井
        TerrestrialPrimary(0x7DB0, 0x9000, "jk1", "ＮＨＫ総合・福井"),
        TerrestrialPrimary(0x7DB1, 0x9008, "jk2", "ＮＨＫＥテレ福井"),
        TerrestrialPrimary(0x7DB2, 0x9010, "jk4", "ＦＢＣテレビ"),
        TerrestrialPrimary(0x7DB3, 0x9018, "jk8", "福井テレビ"),

        // 富山
        TerrestrialPrimary(0x7DA0, 0x9400, "jk1", "ＮＨＫ総合・富山"),
        TerrestrialPrimary(0x7DA1, 0x9408, "jk2", "ＮＨＫＥテレ富山"),
        TerrestrialPrimary(0x7DA2, 0x9410, "jk4", "ＫＮＢ北日本放送"),
        TerrestrialPrimary(0x7DA3, 0x9418, "jk8", "ＢＢＴ富山テレビ"),
        TerrestrialPrimary(0x7DA4, 0x9420, "jk6", "チューリップテレビ"),

        // 三重
        TerrestrialPrimary(0x7D90, 0x9800, "jk1", "ＮＨＫ総合・津"),
        TerrestrialPrimary(0x7D96, 0x9830, "jk7", "三重テレビ"),

        // 岐阜
        TerrestrialPrimary(0x7D80, 0x9C00, "jk1", "ＮＨＫ総合・岐阜"),
        TerrestrialPrimary(0x7D86, 0x9C30, "jk17", "ぎふチャン"),

        // 大阪
        TerrestrialPrimary(0x7D70, 0xA000, "jk1", "ＮＨＫ総合・大阪"),
        TerrestrialPrimary(0x7D76, 0xA030, "jk7", "テレビ大阪"),

        // 京都
        TerrestrialPrimary(0x7D60, 0xA400, "jk1", "ＮＨＫ総合・京都"),
        TerrestrialPrimary(0x7D66, 0xA430, "jk14", "ＫＢＳ京都"),

        // 兵庫
        TerrestrialPrimary(0x7D50, 0xA800, "jk1", "ＮＨＫ総合・神戸"),
        TerrestrialPrimary(0x7D56, 0xA830, "jk13", "サンテレビ"),

        // 和歌山
        TerrestrialPrimary(0x7D40, 0xAC00, "jk1", "ＮＨＫ総合・和歌山"),
        TerrestrialPrimary(0x7D46, 0xAC30, "jk18", "テレビ和歌山"),

        // 奈良
        TerrestrialPrimary(0x7D30, 0xB000, "jk1", "ＮＨＫ総合・奈良"),
        TerrestrialPrimary(0x7D36, 0xB030, "jk19", "奈良テレビ"),

        // 滋賀
        TerrestrialPrimary(0x7D20, 0xB400, "jk1", "ＮＨＫ総合・大津"),
        TerrestrialPrimary(0x7D26, 0xB430, "jk20", "ＢＢＣびわ湖放送"),

        // 広島
        TerrestrialPrimary(0x7D10, 0xB800, "jk1", "ＮＨＫ総合・広島"),
        TerrestrialPrimary(0x7D11, 0xB808, "jk2", "ＮＨＫＥテレ広島"),
        TerrestrialPrimary(0x7D12, 0xB810, "jk6", "ＲＣＣテレビ"),
        TerrestrialPrimary(0x7D13, 0xB818, "jk4", "広島テレビ"),
        TerrestrialPrimary(0x7D14, 0xB820, "jk5", "広島ホームテレビ"),
        TerrestrialPrimary(0x7D15, 0xB828, "jk8", "ＴＳＳ"),

        // 岡山
        TerrestrialPrimary(0x7D00, 0xBC00, "jk1", "ＮＨＫ総合・岡山"),
        TerrestrialPrimary(0x7D01, 0xBC08, "jk2", "ＮＨＫＥテレ岡山"),

        // 島根
        TerrestrialPrimary(0x7CF0, 0xC000, "jk1", "ＮＨＫ総合・松江"),
        TerrestrialPrimary(0x7CF1, 0xC008, "jk2", "ＮＨＫＥテレ松江"),

        // 鳥取
        TerrestrialPrimary(0x7CE0, 0xC400, "jk1", "ＮＨＫ総合・鳥取"),
        TerrestrialPrimary(0x7CE1, 0xC408, "jk2", "ＮＨＫＥテレ鳥取"),

        // 山口
        TerrestrialPrimary(0x7CD0, 0xC800, "jk1", "ＮＨＫ総合・山口"),
        TerrestrialPrimary(0x7CD1, 0xC808, "jk2", "ＮＨＫＥテレ山口"),
        TerrestrialPrimary(0x7CD2, 0xC810, "jk4", "ＫＲＹ山口放送"),
        TerrestrialPrimary(0x7CD3, 0xC818, "jk6", "ｔｙｓテレビ山口"),
        TerrestrialPrimary(0x7CD4, 0xC820, "jk5", "ｙａｂ山口朝日"),

        // 愛媛
        TerrestrialPrimary(0x7CC0, 0xCC00, "jk1", "ＮＨＫ総合・松山"),
        TerrestrialPrimary(0x7CC1, 0xCC08, "jk2", "ＮＨＫＥテレ松山"),
        TerrestrialPrimary(0x7CC2, 0xCC10, "jk4", "南海放送"),
        TerrestrialPrimary(0x7CC3, 0xCC18, "jk5", "愛媛朝日"),
        TerrestrialPrimary(0x7CC4, 0xCC20, "jk6", "あいテレビ"),
        TerrestrialPrimary(0x7CC5, 0xCC28, "jk8", "テレビ愛媛"),

        // 香川
        TerrestrialPrimary(0x7CB0, 0xD000, "jk1", "ＮＨＫ総合・高松"),
        TerrestrialPrimary(0x7CB1, 0xD008, "jk2", "ＮＨＫＥテレ高松"),

        // 徳島
        TerrestrialPrimary(0x7CA0, 0xD400, "jk1", "ＮＨＫ総合・徳島"),
        TerrestrialPrimary(0x7CA1, 0xD408, "jk2", "ＮＨＫEテレ徳島"),
        TerrestrialPrimary(0x7CA2, 0xD410, "jk4", "四国放送"),

        // 高知
        TerrestrialPrimary(0x7C90, 0xD800, "jk1", "ＮＨＫ総合・高知"),
        TerrestrialPrimary(0x7C91, 0xD808, "jk2", "ＮＨＫＥテレ高知"),
        TerrestrialPrimary(0x7C92, 0xD810, "jk4", "高知放送"),
        TerrestrialPrimary(0x7C93, 0xD818, "jk6", "テレビ高知"),
        TerrestrialPrimary(0x7C94, 0xD820, "jk8", "さんさんテレビ"),

        // 福岡
        TerrestrialPrimary(0x7C80, 0xDC00, "jk1", "ＮＨＫ総合・福岡"),
        TerrestrialPrimary(0x7880, 0xDE00, "jk1", "ＮＨＫ総合・北九州"),
        TerrestrialPrimary(0x7C81, 0xDC08, "jk2", "ＮＨＫＥテレ福岡"),
        TerrestrialPrimary(0x7881, 0xDE08, "jk2", "ＮＨＫＥテレ北九州"),
        TerrestrialPrimary(0x7C82, 0xDC10, "jk5", "ＫＢＣ九州朝日放送"),
        TerrestrialPrimary(0x7C83, 0xDC18, "jk6", "ＲＫＢ毎日放送"),
        TerrestrialPrimary(0x7C84, 0xDC20, "jk4", "ＦＢＳ福岡放送"),
        TerrestrialPrimary(0x7C85, 0xDC28, "jk7", "ＴＶＱ九州放送"),
        TerrestrialPrimary(0x7C86, 0xDC30, "jk8", "ＴＮＣテレビ西日本"),

        // 熊本
        TerrestrialPrimary(0x7C70, 0xE000, "jk1", "ＮＨＫ総合・熊本"),
        TerrestrialPrimary(0x7C71, 0xE008, "jk2", "ＮＨＫＥテレ熊本"),
        TerrestrialPrimary(0x7C72, 0xE010, "jk6", "ＲＫＫ熊本放送"),
        TerrestrialPrimary(0x7C73, 0xE018, "jk8", "ＴＫＵテレビ熊本"),
        TerrestrialPrimary(0x7C74, 0xE020, "jk4", "ＫＫＴくまもと県民"),
        TerrestrialPrimary(0x7C75, 0xE028, "jk5", "ＫＡＢ熊本朝日放送"),

        // 長崎
        TerrestrialPrimary(0x7C60, 0xE400, "jk1", "ＮＨＫ総合・長崎"),
        TerrestrialPrimary(0x7C61, 0xE408, "jk2", "ＮＨＫＥテレ長崎"),
        TerrestrialPrimary(0x7C62, 0xE410, "jk6", "ＮＢＣ長崎放送"),
        TerrestrialPrimary(0x7C63, 0xE418, "jk8", "ＫＴＮテレビ長崎"),
        TerrestrialPrimary(0x7C64, 0xE420, "jk5", "ＮＣＣ長崎文化放送"),
        TerrestrialPrimary(0x7C65, 0xE428, "jk4", "ＮＩＢ長崎国際テレビ"),

        // 鹿児島
        TerrestrialPrimary(0x7C50, 0xE800, "jk1", "ＮＨＫ総合・鹿児島"),
        TerrestrialPrimary(0x7C51, 0xE808, "jk2", "ＮＨＫＥテレ鹿児島"),
        TerrestrialPrimary(0x7C52, 0xE810, "jk6", "ＭＢＣ南日本放送"),
        TerrestrialPrimary(0x7C53, 0xE818, "jk8", "ＫＴＳ鹿児島テレビ"),
        TerrestrialPrimary(0x7C54, 0xE820, "jk5", "ＫＫＢ鹿児島放送"),
        TerrestrialPrimary(0x7C55, 0xE828, "jk4", "ＫＹＴ鹿児島読売ＴＶ"),

        // 宮崎
        TerrestrialPrimary(0x7C40, 0xEC00, "jk1", "ＮＨＫ総合・宮崎"),
        TerrestrialPrimary(0x7C41, 0xEC08, "jk2", "ＮＨＫＥテレ宮崎"),
        TerrestrialPrimary(0x7C42, 0xEC10, "jk6", "ＭＲＴ宮崎放送"),
        TerrestrialPrimary(0x7C43, 0xEC18, "jk8", "ＵＭＫテレビ宮崎"),

        // 大分
        TerrestrialPrimary(0x7C30, 0xF000, "jk1", "ＮＨＫ総合・大分"),
        TerrestrialPrimary(0x7C31, 0xF008, "jk2", "ＮＨＫＥテレ大分"),
        TerrestrialPrimary(0x7C32, 0xF010, "jk6", "ＯＢＳ大分放送"),
        TerrestrialPrimary(0x7C33, 0xF018, "jk4", "ＴＯＳテレビ大分"),
        TerrestrialPrimary(0x7C34, 0xF020, "jk5", "ＯＡＢ大分朝日放送"),

        // 佐賀
        TerrestrialPrimary(0x7C20, 0xF400, "jk1", "ＮＨＫ総合・佐賀"),
        TerrestrialPrimary(0x7C21, 0xF408, "jk2", "ＮＨＫＥテレ佐賀"),
        TerrestrialPrimary(0x7C22, 0xF410, "jk8", "ＳＴＳサガテレビ"),

        // 沖縄
        TerrestrialPrimary(0x7C10, 0xF800, "jk1", "ＮＨＫ総合・沖縄"),
        TerrestrialPrimary(0x7C11, 0xF808, "jk2", "ＮＨＫＥテレ沖縄"),
        TerrestrialPrimary(0x7C12, 0xF810, "jk6", "ＲＢＣテレビ"),
        TerrestrialPrimary(0x7C14, 0xF820, "jk5", "ＱＡＢ琉球朝日放送"),
        TerrestrialPrimary(0x7C17, 0xF838, "jk8", "沖縄テレビ（ＯＴＶ）"),

        // 関東広域の既知サブチャンネル
        Terrestrial(0x7FE0, 0x0401, "jk1", "NHK総合2・東京", isPrimary: false),
        Terrestrial(0x7FE1, 0x0409, "jk2", "NHK Eテレ2東京", isPrimary: false),
        Terrestrial(0x7FE1, 0x040A, "jk2", "NHK Eテレ3東京", isPrimary: false),
        Terrestrial(0x7FE2, 0x0411, "jk4", "日テレ2", isPrimary: false),
        Terrestrial(0x7FE3, 0x0419, "jk6", "TBS2", isPrimary: false),
        Terrestrial(0x7FE4, 0x0421, "jk8", "フジテレビ2", isPrimary: false),
        Terrestrial(0x7FE4, 0x0422, "jk8", "フジテレビ3", isPrimary: false),
        Terrestrial(0x7FE5, 0x0429, "jk5", "テレビ朝日2", isPrimary: false),
        Terrestrial(0x7FE5, 0x042A, "jk5", "テレビ朝日3", isPrimary: false),
        Terrestrial(0x7FE6, 0x0431, "jk7", "テレ東2", isPrimary: false),
        Terrestrial(0x7FE6, 0x0432, "jk7", "テレ東3", isPrimary: false),
    };

    public static readonly IReadOnlyDictionary<ServiceKey, JkServiceMapEntry> ByServiceKey =
        All.GroupBy(entry => entry.ServiceKey)
            .ToDictionary(group => group.Key, group => group.First());

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<JkServiceMapEntry>> ByJkId =
        All.GroupBy(entry => entry.JkId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JkServiceMapEntry>)group.ToArray(),
                StringComparer.Ordinal);

    public static ServiceKey TerrestrialServiceKey(ushort transportStreamId, ushort serviceId)
        => new(transportStreamId, transportStreamId, serviceId);

    public static ServiceKey SatelliteServiceKey(ushort originalNetworkId, ushort transportStreamId, ushort serviceId)
        => new(originalNetworkId, transportStreamId, serviceId);

    private static JkServiceMapEntry TerrestrialPrimary(ushort transportStreamId, ushort serviceId, string jkId, string? notes = null)
        => Terrestrial(transportStreamId, serviceId, jkId, notes, isPrimary: true);

    private static JkServiceMapEntry Terrestrial(ushort transportStreamId, ushort serviceId, string jkId, string? notes = null, bool isPrimary = true)
        => new(TerrestrialServiceKey(transportStreamId, serviceId), jkId, isPrimary, notes);

    private static JkServiceMapEntry SatellitePrimary(ushort originalNetworkId, ushort transportStreamId, ushort serviceId, string jkId, string? notes = null)
        => Satellite(originalNetworkId, transportStreamId, serviceId, jkId, notes, isPrimary: true);

    private static JkServiceMapEntry Satellite(ushort originalNetworkId, ushort transportStreamId, ushort serviceId, string jkId, string? notes = null, bool isPrimary = true)
        => new(SatelliteServiceKey(originalNetworkId, transportStreamId, serviceId), jkId, isPrimary, notes);
}

