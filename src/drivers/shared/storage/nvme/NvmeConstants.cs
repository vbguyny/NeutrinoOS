// ProtonOS NVMe constants: controller registers, command opcodes and
// queue entry layouts (NVMe Base Specification 1.4/2.0 subset).
namespace ProtonOS.Drivers.Storage.Nvme;

/// <summary>Controller register offsets (BAR0).</summary>
public static class NvmeReg
{
    public const int CAP = 0x00;      // 64-bit
    public const int VS = 0x08;
    public const int INTMS = 0x0C;
    public const int INTMC = 0x10;
    public const int CC = 0x14;
    public const int CSTS = 0x1C;
    public const int AQA = 0x24;
    public const int ASQ = 0x28;      // 64-bit
    public const int ACQ = 0x30;      // 64-bit
}

/// <summary>Controller configuration (CC) bits.</summary>
public static class NvmeCc
{
    public const uint EN = 1u << 0;
    public const uint CSS_NVM = 0u << 4;
    public const uint MPS_4K = 0u << 7;
    public const uint AMS_RR = 0u << 11;
    public const uint IOSQES_64 = 6u << 16;
    public const uint IOCQES_16 = 4u << 20;
}

/// <summary>Controller status (CSTS) bits.</summary>
public static class NvmeCsts
{
    public const uint RDY = 1u << 0;
    public const uint CFS = 1u << 1;
}

/// <summary>Admin command opcodes.</summary>
public static class NvmeAdminOp
{
    public const byte CREATE_SQ = 0x01;
    public const byte CREATE_CQ = 0x05;
    public const byte IDENTIFY = 0x06;
}

/// <summary>NVM command opcodes.</summary>
public static class NvmeNvmOp
{
    public const byte FLUSH = 0x00;
    public const byte WRITE = 0x01;
    public const byte READ = 0x02;
}

/// <summary>IDENTIFY CNS values.</summary>
public static class NvmeIdentify
{
    public const uint NAMESPACE = 0x00;
    public const uint CONTROLLER = 0x01;
}

/// <summary>Queue sizes used by this driver.</summary>
public static class NvmeQueue
{
    public const int ADMIN_QID = 0;
    public const int IO_QID = 1;
    public const int ADMIN_ENTRIES = 16;   // power of two
    public const int IO_ENTRIES = 16;      // power of two
}

/// <summary>Page size used for DMA buffers and PRPs.</summary>
public static class NvmePage
{
    public const int SIZE = 4096;
    public const int SECTORS_PER_PAGE = 8;  // 512-byte LBAs
}

/// <summary>Completion status phase helpers.</summary>
public static class NvmeStatus
{
    public const int PHASE_SHIFT = 0;   // bit 0 of status byte 14
    public const int CODE_MASK = 0xFE;  // bits 1..7 of status byte 0..1
}
