# Batch-mode crash filter: stop at the exception-handler stack-dump loop ONLY
# when its source pointer is garbage (Test-56 double-fault signature), dump
# the handler context, then quit. Normal vector-4 traps auto-continue.
set pagination off
set confirm off
# ProtonOS helper flow: proton-connect opens the single gdbstub connection,
# waits for the kernel to write the debug marker, then loads symbols with
# the right relocation offset.
proton-connect

break *0x8098231
commands
silent
if $rdx > 0x10000
  continue
end
printf "=== GARBAGE DUMP BASE DETECTED ===\n"
printf "rdx=%#lx rbx=%#lx rbp=%#lx rsp=%#lx\n", $rdx, $rbx, $rbp, $rsp
info registers rip rsp rbp rbx rdi rsi rdx rcx rax r8 r9 r10 r11 r12 r13 r14 r15
printf "--- context struct at rbx ---\n"
x/24gx $rbx
printf "--- stack at rsp ---\n"
x/24gx $rsp
bt
printf "=== DUMP COMPLETE ===\n"
end
continue
