#!/usr/bin/env python3
"""Patch the WSL Makefile with the NVMe driver wiring (idempotent)."""
p = '/root/neutrino/Makefile'
s = open(p).read()
if 'NVME_DIR' in s:
    print('already patched')
else:
    s = s.replace(
        'AHCI_DIR := $(DRIVERS_DIR)/shared/storage/ahci',
        'AHCI_DIR := $(DRIVERS_DIR)/shared/storage/ahci\n'
        'NVME_DIR := $(DRIVERS_DIR)/shared/storage/nvme')
    s = s.replace(
        'AHCI_DLL := $(BUILD_DIR)/NeutrinoOS.Drivers.Ahci.dll',
        'AHCI_DLL := $(BUILD_DIR)/NeutrinoOS.Drivers.Ahci.dll\n'
        'NVME_DLL := $(BUILD_DIR)/NeutrinoOS.Drivers.Nvme.dll')
    rule = (
        '# Build NVMe driver (Phase 9)\n'
        'NVME_SRC := $(call rwildcard,$(NVME_DIR),*.cs)\n'
        '$(NVME_DLL): $(NVME_SRC) $(NVME_DIR)/Nvme.csproj $(DDK_DLL) | $(BUILD_DIR)\n'
        '\t@echo "DOTNET build NeutrinoOS.Drivers.Nvme"\n'
        '\tdotnet build $(NVME_DIR)/Nvme.csproj -c Release -o $(BUILD_DIR) --nologo -v q\n'
        '\n'
        '# EXT2 filesystem driver')
    s = s.replace('# EXT2 filesystem driver', rule, 1)
    s = s.replace('$(AHCI_DLL) $(EXT2_DLL) $(TEST_DRIVER_DLL)',
                  '$(AHCI_DLL) $(NVME_DLL) $(EXT2_DLL) $(TEST_DRIVER_DLL)')
    s = s.replace('\tmcopy -i $(IMG) $(AHCI_DLL) ::/drivers/',
                  '\tmcopy -i $(IMG) $(AHCI_DLL) ::/drivers/\n'
                  '\tmcopy -i $(IMG) $(NVME_DLL) ::/drivers/')
    open(p, 'w').write(s)
    print('patched')
