# Stop at the exception-handler stack-dump loop ONLY when its source
# pointer is garbage (the Test-56 double-fault signature); auto-continue
# on the normal intentional traps (vector 4) that every boot produces.
set pagination off
break *0x8098231
commands
silent
if $rdx > 0x10000
  continue
end
printf "=== GARBAGE DUMP BASE: rdx=%#lx rbx=%#lx rsp=%#lx\n", $rdx, $rbx, $rsp
end
continue
