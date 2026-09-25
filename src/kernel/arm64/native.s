// NeutrinoOS kernel - ARM64 (AArch64) native layer (Phase 8, Task 3).
//
// Provides the native symbols the bflat-compiled kernel and korlib
// reference on ARM64, mirroring the x64 native.asm contract:
//
//   - Entry: EfiEntry -> korlib C# EfiMain (x0/x1 untouched)
//   - Codegen helpers: memset / memcpy / RhpStackProbe / GC ref helpers
//   - CPU primitives: barriers, DAIF mask, WFI, counters, TLB, atomics
//   - Context/exception plumbing: capture_context + v1 stubs for the
//     scheduler/JIT/ring3 machinery that arrives in later increments
//     (context switching and interface dispatch are completed during
//     boot bring-up; see docs/PHASE8-ARM64.md)
//
// Built with the clang integrated assembler for aarch64-pc-windows-msvc
// (COFF), linked with lld-link -machine:arm64.

    .text

// ---------------------------------------------------------------------------
// EfiEntry(ImageHandle x0, SystemTable x1) -> korlib EfiMain
// ---------------------------------------------------------------------------
    .globl EfiEntry
EfiEntry:
    b EfiMain

// ---------------------------------------------------------------------------
// Raw PL011 debug output (QEMU virt UART at 0x09000000), reentrancy-free.
// x0 = NUL-terminated byte string.
// ---------------------------------------------------------------------------
    .globl arm64_raw_puts
arm64_raw_puts:
    movz x16, #0x0900, lsl #16
1:
    ldrb w17, [x0], #1
    cbz w17, 2f
    strb w17, [x16]
    b 1b
2:
    ret

arm64_raw_puts_nl:
    movz x16, #0x0900, lsl #16
    mov w17, #13
    strb w17, [x16]
    mov w17, #10
    strb w17, [x16]
    ret

// Fatal halt with a message; never returns. x0 = message.
    .globl arm64_fatal
arm64_fatal:
    bl arm64_raw_puts
    bl arm64_raw_puts_nl
1:
    wfi
    b 1b

// Fatal entries with fixed messages.
    .globl arm64_fatal_throw
arm64_fatal_throw:
    adrp x0, msg_throw
    add x0, x0, :lo12:msg_throw
    b arm64_fatal

    .globl arm64_fatal_rethrow
arm64_fatal_rethrow:
    adrp x0, msg_rethrow
    add x0, x0, :lo12:msg_rethrow
    b arm64_fatal

    .globl arm64_fatal_iface
arm64_fatal_iface:
    adrp x0, msg_iface
    add x0, x0, :lo12:msg_iface
    b arm64_fatal

    .globl arm64_fatal_ring3
arm64_fatal_ring3:
    adrp x0, msg_ring3
    add x0, x0, :lo12:msg_ring3
    b arm64_fatal

    .globl arm64_fatal_funclet
arm64_fatal_funclet:
    adrp x0, msg_funclet
    add x0, x0, :lo12:msg_funclet
    b arm64_fatal

// ---------------------------------------------------------------------------
// memset / memcpy / RhpStackProbe (bflat codegen helpers)
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

    .globl memcpy
memcpy:
    mov x3, x0
    cbz x2, 3f
    cmp x0, x1
    b.lo 1f
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

// ---------------------------------------------------------------------------
// GC reference helpers: plain stores (the kernel GC does not relocate
// concurrently), signatures follow the NativeAOT arm64 convention.
// ---------------------------------------------------------------------------
    .globl RhpAssignRefArm64
RhpAssignRefArm64:
    str x1, [x0]
    ret

    .globl RhpCheckedAssignRefArm64
RhpCheckedAssignRefArm64:
    str x2, [x1]
    ret

// ---------------------------------------------------------------------------
// CPU primitives
// ---------------------------------------------------------------------------
    .globl cli
cli:
    msr daifset, #2
    ret

    .globl sti
sti:
    msr daifclr, #2
    ret

    .globl hlt
hlt:
    wfi
    ret

    .globl pause
pause:
    yield
    ret

    .globl int3
int3:
    brk #0
    ret

    .globl mfence
mfence:
    dmb sy
    ret

    .globl rdtsc_native
rdtsc_native:
    mrs x0, cntvct_el0
    ret

    .globl read_flags
read_flags:
    mrs x0, daif
    ret

    .globl get_rsp
get_rsp:
    mov x0, sp
    ret

    .globl invlpg
invlpg:
    tlbi vale1is, x0
    dsb ish
    isb
    ret

    .globl read_cr2
read_cr2:
    mrs x0, far_el1
    ret

    .globl read_cr3
read_cr3:
    mrs x0, ttbr0_el1
    ret

    .globl write_cr3
write_cr3:
    msr ttbr0_el1, x0
    dsb ish
    tlbi vmalle1is
    dsb ish
    isb
    ret

// Port I/O does not exist on ARM64: return 0 / ignore. Reached only by
// legacy x64 device paths (16550/PS2/VGA/PCI CF8), none of which bind here.
    .globl inb
inb:
    mov w0, #0
    ret

    .globl inw
inw:
    mov w0, #0
    ret

    .globl ind
ind:
    mov w0, #0
    ret

    .globl outb
outb:
    ret

    .globl outw
outw:
    ret

    .globl outd
outd:
    ret

// MSRs (x64 concept): tolerated no-ops on ARM64.
    .globl rdmsr
rdmsr:
    mov x0, #0
    ret

    .globl wrmsr
wrmsr:
    ret

// Descriptor tables (x64 concept): no-ops.
    .globl lgdt
lgdt:
    ret

    .globl lidt
lidt:
    ret

    .globl ltr
ltr:
    ret

    .globl reload_segments
reload_segments:
    ret

// FP state helpers: stubs in this increment (the scheduler does not swap
// FP state yet on ARM64; noted as a limitation in PHASE8-ARM64.md).
    .globl fxsave
fxsave:
    ret

    .globl fxrstor
fxrstor:
    ret

// ---------------------------------------------------------------------------
// Atomics (LDXR/STXR loops; all return the ORIGINAL value)
// ---------------------------------------------------------------------------
    .globl atomic_cmpxchg32
atomic_cmpxchg32:
1:
    ldaxr w3, [x0]
    cmp w3, w2
    b.ne 2f
    stlxr w4, w1, [x0]
    cbnz w4, 1b
2:
    mov w0, w3
    ret

    .globl atomic_cmpxchg64
atomic_cmpxchg64:
1:
    ldaxr x3, [x0]
    cmp x3, x2
    b.ne 2f
    stlxr w4, x1, [x0]
    cbnz w4, 1b
2:
    mov x0, x3
    ret

    .globl atomic_xchg32
atomic_xchg32:
1:
    ldaxr w2, [x0]
    stlxr w3, w1, [x0]
    cbnz w3, 1b
    mov w0, w2
    ret

    .globl atomic_xchg64
atomic_xchg64:
1:
    ldaxr x2, [x0]
    stlxr w3, x1, [x0]
    cbnz w3, 1b
    mov x0, x2
    ret

    .globl atomic_add32
atomic_add32:
1:
    ldaxr w2, [x0]
    add w3, w2, w1
    stlxr w4, w3, [x0]
    cbnz w4, 1b
    mov w0, w2
    ret

    .globl atomic_add64
atomic_add64:
1:
    ldaxr x2, [x0]
    add x3, x2, x1
    stlxr w4, x3, [x0]
    cbnz w4, 1b
    mov x0, x2
    ret

// ---------------------------------------------------------------------------
// Boot info slot (Kernel.BootInfoAccess reads the pointer via get_boot_info)
// ---------------------------------------------------------------------------
    .globl get_boot_info
get_boot_info:
    adrp x0, g_boot_info_slot
    add x0, x0, :lo12:g_boot_info_slot
    ret

// ---------------------------------------------------------------------------
// Context plumbing (v1 stubs; real switch lands with the scheduler pass)
// ---------------------------------------------------------------------------
    .globl switch_context
switch_context:
    ret

    .globl load_context
load_context:
    ret

    .globl kernel_context_save
kernel_context_save:
    ret

    .globl kernel_context_restore
kernel_context_restore:
    ret

    .globl restore_pal_context
restore_pal_context:
    ret

// int run_on_big_stack(ulong stackTop x0, ulong fn x1, ulong arg x2)
    .globl run_on_big_stack
run_on_big_stack:
    mov x9, x0
    mov x0, x2
    mov x5, sp
    mov sp, x9
    blr x1
    mov sp, x5
    ret

// Capture Rip/Rsp/Rbp into ExceptionContext (offsets 0/8/16).
// void capture_context(ExceptionContext* x0)
    .globl capture_context
capture_context:
    str x30, [x0]
    mov x9, sp
    str x9, [x0, #8]
    str x29, [x0, #16]
    ret

// Syscall entry points / ring3 transitions: not available in this increment.
    .globl set_syscall_kernel_stack
set_syscall_kernel_stack:
    ret

    .globl get_syscall_entry
get_syscall_entry:
    mov x0, #0
    ret

    .globl jump_to_ring3
jump_to_ring3:
    b arm64_fatal_ring3

// ulong jump_to_ring3_with_retval(userRip x0, userRsp x1, retval x2)
    .globl jump_to_ring3_with_retval
jump_to_ring3_with_retval:
    b arm64_fatal_ring3

// ---------------------------------------------------------------------------
// Exception ABI entry points (managed exceptions arrive with the exception
// pass; the v1 handlers report and halt rather than silently corrupt state)
// ---------------------------------------------------------------------------
    .globl RhpThrowEx
RhpThrowEx:
    b arm64_fatal_throw

    .globl RhpRethrow
RhpRethrow:
    b arm64_fatal_rethrow

    .globl RhpInitialDynamicInterfaceDispatch
RhpInitialDynamicInterfaceDispatch:
    b arm64_fatal_iface

    .globl call_filter_funclet
call_filter_funclet:
    b arm64_fatal_funclet

    .globl call_finally_handler
call_finally_handler:
    b arm64_fatal_funclet

// ---------------------------------------------------------------------------
// JIT registration hooks + shim address words (JIT compilation is x64-only;
// the kernel never executes these on ARM64)
// ---------------------------------------------------------------------------
    .globl __proton_jit_register
__proton_jit_register:
    ret

    .globl __proton_jit_set_action
__proton_jit_set_action:
    ret

    .globl __proton_jit_set_relevant_entry
__proton_jit_set_relevant_entry:
    ret

    .globl __proton_jit_set_first_entry
__proton_jit_set_first_entry:
    ret

    .globl __proton_jit_get_first_entry
__proton_jit_get_first_entry:
    mov x0, #0
    ret

// ---------------------------------------------------------------------------
// SMP trampoline placeholders (PSCI bring-up is a later increment)
// ---------------------------------------------------------------------------
    .globl get_ap_trampoline_size
get_ap_trampoline_size:
    mov x0, #0
    ret

    .globl get_ap_trampoline_start
get_ap_trampoline_start:
    mov x0, #0
    ret

    .globl get_ap_startup_data
get_ap_startup_data:
    mov x0, #0
    ret

    .globl get_isr_table
get_isr_table:
    mov x0, #0
    ret

// ---------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------
    .data

    .globl g_boot_info_slot
g_boot_info_slot:
    .xword 0

// JIT shim address words (the JIT emits code that loads these; never
// executed on ARM64 - point at a plain `ret`).
    .globl jit_align_call_addr
jit_align_call_addr:
    .xword arm64_stub_ret

    .globl jit_ensure_compiled_shim_addr
jit_ensure_compiled_shim_addr:
    .xword arm64_stub_ret

    .globl jit_ensure_virtual_compiled_shim_addr
jit_ensure_virtual_compiled_shim_addr:
    .xword arm64_stub_ret

    .globl jit_ensure_vtable_slot_compiled_shim_addr
jit_ensure_vtable_slot_compiled_shim_addr:
    .xword arm64_stub_ret

    .globl jit_get_interface_method_shim_addr
jit_get_interface_method_shim_addr:
    .xword arm64_stub_ret

    .globl jit_new_array_shim_addr
jit_new_array_shim_addr:
    .xword arm64_stub_ret

    .globl jit_new_fast_shim_addr
jit_new_fast_shim_addr:
    .xword arm64_stub_ret

    .text
    .globl arm64_stub_ret
arm64_stub_ret:
    ret

msg_throw:
    .asciz "[arm64] FATAL: managed exception (RhpThrowEx) - exception support arrives with the ARM64 exception pass"
msg_rethrow:
    .asciz "[arm64] FATAL: managed rethrow (RhpRethrow)"
msg_iface:
    .asciz "[arm64] FATAL: RhpInitialDynamicInterfaceDispatch (interface dispatch unavailable in this increment)"
msg_ring3:
    .asciz "[arm64] FATAL: ring3 transition not supported yet"
msg_funclet:
    .asciz "[arm64] FATAL: exception funclet call (EH pass pending)"
