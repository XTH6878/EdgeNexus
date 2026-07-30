using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public sealed record ProtocolPointAddressExample(
    string Address,
    string Area,
    TagDataType DataType,
    string Purpose,
    ModbusFunctionCode? ModbusFunctionCode = null);

public sealed record ProtocolPointAddressGuide(
    string Summary,
    string AddressPlaceholder,
    IReadOnlyList<ProtocolPointAddressExample> Examples,
    IReadOnlyList<string> Notes);

public static class ProtocolPointAddressCatalog
{
    private static readonly IReadOnlyDictionary<ProtocolKind, ProtocolPointAddressGuide> Guides =
        new Dictionary<ProtocolKind, ProtocolPointAddressGuide>
        {
            [ProtocolKind.SiemensS7] = Guide(
                "使用西门子工程地址；DB、M、I、Q 区均按字节偏移填写，位地址必须包含位号。",
                "例如 DB1.DBW2 / DB1.DBX0.0 / M10.0",
                [
                    Example("DB1.DBX0.0", "DB 位", TagDataType.Bool, "DB1 第0字节第0位"),
                    Example("DB1.DBW2", "DB 字", TagDataType.UInt16, "DB1 第2字节开始的16位值"),
                    Example("DB1.DBD4", "DB 双字", TagDataType.Float32, "DB1 第4字节开始的32位浮点数"),
                    Example("M10.0", "M 位", TagDataType.Bool, "M10.0 中间继电器"),
                    Example("VD100", "S7-200 V区", TagDataType.Float32, "V100开始的32位浮点数")
                ],
                [
                    "S7-1200/1500 的 DB 块需关闭优化访问，并允许 PUT/GET 通信。",
                    "S7-200/SMART 常用 V/VB/VW/VD；其他系列常用 DB。不要把 TIA 符号名当作绝对地址。"
                ]),
            [ProtocolKind.ModbusTcp] = Guide(
                "地址栏只填写从0开始的线圈或寄存器偏移，功能码在独立列选择。",
                "例如 0 / 100 / 200",
                [
                    Example("0", "线圈", TagDataType.Bool, "第1个线圈", ModbusFunctionCode.ReadCoils),
                    Example("0", "离散输入", TagDataType.Bool, "第1个离散输入", ModbusFunctionCode.ReadDiscreteInputs),
                    Example("0", "保持寄存器", TagDataType.UInt16, "第1个保持寄存器", ModbusFunctionCode.ReadHoldingRegisters),
                    Example("2", "保持寄存器", TagDataType.Float32, "从偏移2开始占2个寄存器", ModbusFunctionCode.ReadHoldingRegisters),
                    Example("0", "输入寄存器", TagDataType.Int16, "第1个输入寄存器", ModbusFunctionCode.ReadInputRegisters)
                ],
                [
                    "设备手册若使用 40001 表示法，本页面通常填写 0；40002 通常填写 1。",
                    "功能码01/02只能配 Bool；03/04用于寄存器数据，32/64位值会连续占用多个寄存器。"
                ]),
            [ProtocolKind.MitsubishiMc] = Guide(
                "支持三菱MC 3E帧与A兼容1E帧，使用三菱软元件原生地址。",
                "例如 D100 / M100 / X0",
                [
                    Example("D100", "数据寄存器", TagDataType.Int16, "D100 字数据"),
                    Example("D200", "数据寄存器", TagDataType.Float32, "D200-D201 32位浮点数"),
                    Example("M100", "内部继电器", TagDataType.Bool, "M100 位状态"),
                    Example("X0", "输入继电器", TagDataType.Bool, "X0 输入状态"),
                    Example("Y0", "输出继电器", TagDataType.Bool, "Y0 输出状态")
                ],
                [
                    "X/Y 地址进制随 PLC 系列和工程设置变化，必须与 GX Works 点表保持一致。",
                    "3E帧支持TCP/UDP；A兼容1E帧仅支持TCP，并且只使用PLC编号。",
                    "A兼容1E帧不使用网络号、网络站号和目标IO号，适用于A系列、FX3U兼容模块等设备。",
                    "UDP没有连接握手，诊断时必须配置至少一个有效点位，并以PLC返回报文作为通信成功依据。"
                ]),
            [ProtocolKind.OpcUa] = Guide(
                "填写服务器返回的完整 NodeId，不填写 BrowsePath 或界面显示名称。",
                "例如 ns=2;s=Machine.Speed",
                [
                    Example("ns=2;s=Machine.Speed", "字符串 NodeId", TagDataType.Float32, "设备速度变量"),
                    Example("ns=2;i=1001", "数字 NodeId", TagDataType.Int32, "命名空间2中的数字节点"),
                    Example("ns=3;s=Line1.Running", "字符串 NodeId", TagDataType.Bool, "产线运行状态")
                ],
                [
                    "命名空间索引 ns 由 OPC UA 服务器决定，重部署服务器后应核对是否变化。",
                    "最终值类型由服务器节点决定；本地数据类型用于配置审查和后续处理。"
                ]),
            [ProtocolKind.PanasonicMewtocol] = Guide(
                "使用松下 Mewtocol 软元件地址；DT/LD 为字区，R/X/Y 为位区。",
                "例如 DT100 / R0 / X0",
                [
                    Example("DT100", "数据寄存器", TagDataType.Int16, "DT100 字数据"),
                    Example("DT200", "数据寄存器", TagDataType.Float32, "DT200-DT201 32位浮点数"),
                    Example("R0", "内部继电器", TagDataType.Bool, "R0 位状态"),
                    Example("X0", "输入", TagDataType.Bool, "X0 输入状态"),
                    Example("Y0", "输出", TagDataType.Bool, "Y0 输出状态")
                ],
                [
                    "点位前缀必须与 FP 系列 PLC 的实际软元件区一致。",
                    "32/64位数据从起始字连续读取，需确认 PLC 内存排列和字节序。"
                ]),
            [ProtocolKind.OmronFins] = Guide(
                "使用欧姆龙 FINS 内存区地址；点号后为位偏移，未带位号时按字读取。",
                "例如 D100 / D100.03 / CIO100",
                [
                    Example("D100", "DM 区", TagDataType.Int16, "D100 字数据"),
                    Example("D100.03", "DM 位", TagDataType.Bool, "D100 第3位"),
                    Example("CIO100", "CIO 区", TagDataType.UInt16, "CIO100 字数据"),
                    Example("W100", "WR 区", TagDataType.UInt16, "W100 工作区字"),
                    Example("H100.00", "HR 位", TagDataType.Bool, "H100 第0位")
                ],
                [
                    "位号范围为00-15；节点号 DA1/SA1 必须与 PLC 网络配置一致。",
                    "跨网段或经路由器通信时，同时核对 DNA/SNA 与 FINS 路由表。"
                ]),
            [ProtocolKind.OmronCip] = Guide(
                "填写欧姆龙 NJ/NX 控制器已发布的 CIP 变量名，可使用结构成员和数组索引。",
                "例如 Machine.Speed / Alarm[0]",
                [
                    Example("Machine.Speed", "结构成员", TagDataType.Float32, "Machine 结构的 Speed 成员"),
                    Example("Machine.Running", "结构成员", TagDataType.Bool, "Machine 结构的运行状态"),
                    Example("Alarm[0]", "数组元素", TagDataType.Bool, "Alarm 数组第0项"),
                    Example("ProductionCount", "全局变量", TagDataType.Int32, "控制器全局计数")
                ],
                [
                    "变量必须在 Sysmac Studio 中开放外部访问，名称和大小写需完全一致。",
                    "结构体和大数组建议拆分点位，避免单次响应超过控制器报文限制。"
                ]),
            [ProtocolKind.AllenBradleyEtherNetIp] = Guide(
                "填写 ControlLogix/CompactLogix 控制器标签名，支持程序标签、结构成员和数组索引。",
                "例如 Motor.Speed / Counter[0]",
                [
                    Example("Motor.Speed", "结构成员", TagDataType.Float32, "Motor 结构的 Speed 成员"),
                    Example("Motor.Running", "结构成员", TagDataType.Bool, "Motor 运行状态"),
                    Example("Counter[0]", "数组元素", TagDataType.Int32, "Counter 数组第0项"),
                    Example("Program:MainProgram.BatchId", "程序标签", TagDataType.Int32, "MainProgram 内的 BatchId")
                ],
                [
                    "标签 External Access 必须允许读取，名称和大小写需与 Studio 5000 一致。",
                    "经背板或网桥访问时核对 CPU 槽号和 CIP 路由路径。"
                ]),
            [ProtocolKind.BeckhoffAds] = Guide(
                "优先使用TwinCAT符号变量地址s=变量名，也支持I/Q/M绝对区、绝对内存和Index Group地址。",
                "例如 s=MAIN.rSpeed / M100.0 / IG=0xF020;0",
                [
                    Example("s=MAIN.bRun", "符号变量", TagDataType.Bool, "MAIN程序运行状态"),
                    Example("s=MAIN.nCount", "符号变量", TagDataType.Int32, "MAIN程序生产计数"),
                    Example("s=MAIN.rSpeed", "符号变量", TagDataType.Float32, "MAIN程序速度设定"),
                    Example("M100.0", "M绝对位", TagDataType.Bool, "M区第100字节第0位"),
                    Example("IG=0xF020;0", "Index Group", TagDataType.UInt16, "自定义Index Group偏移0")
                ],
                [
                    "TwinCAT 2常用AMS端口801，TwinCAT 3 Runtime 1常用851；必须与实际Runtime一致。",
                    "符号变量名称和大小写必须与PLC工程一致，PLC下载或在线修改后建议让驱动重连以刷新句柄缓存。",
                    "ADS跨主机通信必须在TwinCAT路由表中建立双方AMS Route，仅开放TCP端口不能替代路由配置。"
                ]),
            [ProtocolKind.KeyenceMc] = Guide(
                "使用基恩士 KV 系列 MC 软元件地址，DM 为字区，MR/R/X/Y 为位区。",
                "例如 DM100 / MR100 / R0",
                [
                    Example("DM100", "数据存储器", TagDataType.Int16, "DM100 字数据"),
                    Example("DM200", "数据存储器", TagDataType.Float32, "DM200-DM201 32位浮点数"),
                    Example("R015", "内部继电器", TagDataType.Bool, "R015 位状态"),
                    Example("MR015", "内部辅助继电器", TagDataType.Bool, "MR015 位状态"),
                    Example("LR015", "锁存继电器", TagDataType.Bool, "LR015 位状态")
                ],
                [
                    "软元件范围和进制以 KV STUDIO 对应 CPU 型号为准。",
                    "通信单元需启用 MC 协议兼容服务并放行配置端口。"
                ]),
            [ProtocolKind.InovanceTcp] = Guide(
                "地址区随 PLC 系列变化；样例已标明适用系列，必须与设备参数中的系列一致。",
                "AM: SD100 / Q0；H3U/H5U: D100 / X0",
                [
                    Example("SD100", "AM · 系统字", TagDataType.Int16, "AM系列 SD100 字数据"),
                    Example("Q0", "AM · 输出位", TagDataType.Bool, "AM系列 Q0 输出状态"),
                    Example("M100", "AM/H3U/H5U · M位", TagDataType.Bool, "M100 内部位状态"),
                    Example("D100", "H3U/H5U · 数据字", TagDataType.Int16, "H系列 D100 字数据"),
                    Example("X0", "H3U/H5U · 输入位", TagDataType.Bool, "H系列 X0 输入状态")
                ],
                [
                    "AM 字区使用 SM/SD、位区使用 Q/I/M；H3U/H5U 常用 D/R 与 M/X/Y。",
                    "32/64位数据占用连续寄存器，字节序需与 PLC 实际写入方式一致。"
                ]),
            [ProtocolKind.DeltaTcp] = Guide(
                "使用台达 PLC 软元件地址；D 为字区，M/X/Y/S 为位区。",
                "例如 D100 / M100 / X0",
                [
                    Example("D100", "数据寄存器", TagDataType.Int16, "D100 字数据"),
                    Example("D200", "数据寄存器", TagDataType.Float32, "D200-D201 32位浮点数"),
                    Example("M100", "内部继电器", TagDataType.Bool, "M100 位状态"),
                    Example("X0", "输入", TagDataType.Bool, "X0 输入状态"),
                    Example("Y0", "输出", TagDataType.Bool, "Y0 输出状态")
                ],
                [
                    "DVP 与 AS 系列的地址范围和扩展区不同，必须与 ISPSoft/DIAStudio 点表一致。",
                    "当前 HslCommunication 12.9.1 按 X0/Y0 格式映射位地址；升级驱动后由地址解析测试复核兼容性。"
                ])
        };

    public static ProtocolPointAddressGuide Get(ProtocolKind protocol) =>
        Guides.TryGetValue(protocol, out var guide)
            ? guide
            : throw new NotSupportedException($"协议 {protocol} 尚未定义点位地址规范。");

    public static IReadOnlyDictionary<ProtocolKind, ProtocolPointAddressGuide> GetAll() => Guides;

    private static ProtocolPointAddressGuide Guide(
        string summary,
        string addressPlaceholder,
        ProtocolPointAddressExample[] examples,
        string[] notes) => new(summary, addressPlaceholder, examples, notes);

    private static ProtocolPointAddressExample Example(
        string address,
        string area,
        TagDataType dataType,
        string purpose,
        ModbusFunctionCode? functionCode = null) => new(address, area, dataType, purpose, functionCode);
}
