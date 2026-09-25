// NeutrinoOS kernel - ARM64 (AArch64) native layer (Phase 8, Task 3).
//
// Minimal bring-up set, mirroring the x64 native.asm contract for the
// symbols the bflat-compiled kernel and korlib reference:
//   memset / memcpy      - bflat codegen helpers
//   RhpStackProbe        - stack probing emitted by codegen for large frames
//   EfiEntry             - PE entry; forwards to korlib's C# EfiMain with
//                          the firmware-provided (x0, x1) untouched
//
// Built with the clang integrated assembler for aarch64-pc-windows-msvc
// (COFF), linked with lld-link -machine:arm64.

    .text

// ---------------------------------------------------------------------------
// EfiEntry(ImageHandle x0, SystemTable x1)
// AArch64 UEFI passes the two arguments in x0/x1, which is exactly the
// C# EfiMain(IntPtr, EFI_SYSTEM_TABLE*) calling convention, so a tail
// branch suffices.
// ---------------------------------------------------------------------------
    .globl EfiEntry
EfiEntry:
    b EfiMain

// ---------------------------------------------------------------------------
// void* memset(void* dst x0, int c w1, unsigned long n x2)
// ---------------------------------------------------------------------------
    .globl memset
memset:
    cbz x2, 2f
    and w1, w1, #0xFF
1:
    strb w1, [x0], #1
    subs x2, x2, #1
    b.ne 1b
2:
    ret

// ---------------------------------------------------------------------------
// void* memcpy(void* dst x0, const void* src x1, unsigned long n x2)
// (overlap-safe enough for the runtime's use; forward/backward chosen by
// comparing the pointers)
// ---------------------------------------------------------------------------
    .globl memcpy
memcpy:
    mov x3, x0              // keep dst for return
    cbz x2, 3f
    cmp x0, x1
    b.lo 1f
    // dst >= src: copy backwards to stay safe on overlap
    add x0, x0, x2
    add x1, x1, x2
2:
    ldrb w4, [x1, #-1]!
    strb w4, [x0, #-1]!
    subs x2, x2, #1
    b.ne 2b
    b 3f
1:
    ldrb w4, [x1], #1
    strb w4, [x0], #1
    subs x2, x2, #1
    b.ne 1b
3:
    mov x0, x3
    ret

// ---------------------------------------------------------------------------
// RhpStackProbe(unsigned long n x0) - touch each page of a large frame.
// ---------------------------------------------------------------------------
    .globl RhpStackProbe
RhpStackProbe:
    cbz x0, 2f
    mov x1, sp
1:
    sub x1, x1, #4096
    str xzr, [x1]
    subs x0, x0, #4096
    b.hi 1b
2:
    ret

    .section .note.GNU-stack,"",%progbits
