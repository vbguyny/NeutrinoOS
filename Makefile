# NeutrinoOS Makefile (console-only fork of ProtonOS)

# Default target architecture
ARCH ?= x64

# Directories
BUILD_DIR := build/$(ARCH)
KERNEL_DIR := src/kernel
KORLIB_DIR := src/korlib
JITTEST_DIR := src/JITTest

# Bootable disk image produced by 'make image'
IMG := $(BUILD_DIR)/neutrinoos.img

# Output files
ifeq ($(ARCH),x64)
    EFI_NAME := BOOTX64.EFI
    KERNEL_NAME := KERNEL.BIN
    NASM_FORMAT := win64
else ifeq ($(ARCH),arm64)
    EFI_NAME := BOOTAA64.EFI
    KERNEL_NAME := KERNEL.BIN
    $(error ARM64 not yet implemented)
endif

# Bootloader
BOOTLOADER_DIR := src/bootloader
BOOTLOADER_SRC := $(BOOTLOADER_DIR)/boot.asm
BOOTLOADER_OBJ := $(BUILD_DIR)/boot.obj
BOOTLOADER_EFI := $(BUILD_DIR)/LOADER.EFI

# Tools
NASM := nasm
LD := lld-link
# Use local bflat build with custom ILCompiler (for testing fixes)
# To use system bflat, change to: BFLAT := bflat
BFLAT := dotnet $(CURDIR)/tools/bflat/src/bflat/bin/Release/net10.0/bflat.dll

# Tool flags
NASM_FLAGS := -f $(NASM_FORMAT)
LD_FLAGS := -subsystem:efi_application -entry:EfiEntry

BFLAT_FLAGS := \
	--os:uefi \
	--arch:$(ARCH) \
	--stdlib:none \
	--no-stacktrace-data \
	--no-globalization \
	--no-reflection \
	--no-exception-messages \
	--emit-eh-info

# Architecture-specific defines
ifeq ($(ARCH),x64)
    BFLAT_FLAGS += -d ARCH_X64 -d BOOT_UEFI
else ifeq ($(ARCH),arm64)
    BFLAT_FLAGS += -d ARCH_ARM64 -d BOOT_UEFI
else ifeq ($(ARCH),apple)
    BFLAT_FLAGS += -d ARCH_ARM64 -d BOOT_M1N1
endif

# Recursive wildcard function
rwildcard=$(foreach d,$(wildcard $(1:=/*)),$(call rwildcard,$d,$2) $(filter $(subst *,%,$2),$d))

# Source files
NATIVE_SRC := $(wildcard $(KERNEL_DIR)/$(ARCH)/*.asm)
# Filter out obj/ and bin/ directories from korlib (dotnet build artifacts)
KORLIB_SRC := $(filter-out %/obj/% %/bin/%,$(call rwildcard,$(KORLIB_DIR),*.cs))
KERNEL_SRC := $(call rwildcard,$(KERNEL_DIR),*.cs)

# Object files
NATIVE_OBJ := $(BUILD_DIR)/native.obj
KERNEL_OBJ := $(BUILD_DIR)/kernel.obj

# Test assembly output
JITTEST_DLL := $(BUILD_DIR)/JITTest.dll

# korlib IL assembly (for JIT generic instantiation)
KORLIB_DLL := $(BUILD_DIR)/korlib.dll

# TestSupport assembly (cross-assembly test helpers)
TESTSUPPORT_DIR := src/TestSupport
TESTSUPPORT_DLL := $(BUILD_DIR)/TestSupport.dll

# DDK assembly (JIT library)
DDK_DIR := src/ddk
DDK_DLL := $(BUILD_DIR)/ProtonOS.DDK.dll

# Standard libraries
LIB_DIR := src/lib
PROTONOS_NET_DIR := $(LIB_DIR)/ProtonOS.Net
PROTONOS_NET_DLL := $(BUILD_DIR)/ProtonOS.Net.dll

# Application test assembly
APPTEST_DIR := src/AppTest
APPTEST_DLL := $(BUILD_DIR)/AppTest.dll

# Hello test application (for execve testing - no args)
HELLOAPP_DIR := src/HelloApp
HELLOAPP_DLL := $(BUILD_DIR)/HelloApp.dll

# Args test application (for execve testing with args)
ARGSAPP_DIR := src/ArgsApp
ARGSAPP_DLL := $(BUILD_DIR)/ArgsApp.dll

# Console I/O acceptance test (Phase 2)
CONSOLETEST_DIR := tests/ConsoleIoTest
CONSOLETEST_DLL := $(BUILD_DIR)/console_io_test.dll

# OVMF firmware paths (used by run-qemu-serial targets)
OVMF_CODE := /usr/share/OVMF/OVMF_CODE_4M.fd
OVMF_VARS_SRC := /usr/share/OVMF/OVMF_VARS_4M.fd
OVMF_VARS := $(BUILD_DIR)/OVMF_VARS.fd

# Driver directories
DRIVERS_DIR := src/drivers
VIRTIO_DIR := $(DRIVERS_DIR)/shared/virtio
VIRTIO_BLK_DIR := $(DRIVERS_DIR)/shared/storage/virtio-blk
VIRTIO_NET_DIR := $(DRIVERS_DIR)/shared/network/virtio-net
FAT_DIR := $(DRIVERS_DIR)/shared/storage/fat
AHCI_DIR := $(DRIVERS_DIR)/shared/storage/ahci
EXT2_DIR := $(DRIVERS_DIR)/shared/storage/ext2
TEST_DRIVER_DIR := $(DRIVERS_DIR)/shared/test

# Driver DLLs
VIRTIO_DLL := $(BUILD_DIR)/ProtonOS.Drivers.Virtio.dll
VIRTIO_BLK_DLL := $(BUILD_DIR)/ProtonOS.Drivers.VirtioBlk.dll
VIRTIO_NET_DLL := $(BUILD_DIR)/ProtonOS.Drivers.VirtioNet.dll
FAT_DLL := $(BUILD_DIR)/ProtonOS.Drivers.Fat.dll
AHCI_DLL := $(BUILD_DIR)/ProtonOS.Drivers.Ahci.dll
EXT2_DLL := $(BUILD_DIR)/ProtonOS.Drivers.Ext2.dll
TEST_DRIVER_DLL := $(BUILD_DIR)/ProtonOS.Drivers.Test.dll

# Targets
.PHONY: all clean native kernel bootloader korlibdll testsupport ddk protonos-net apptest drivers consoletest image run run-qemu run-qemu-serial run-qemu-serial-log run-vbox deps install-deps check-deps

all: $(BUILD_DIR)/$(EFI_NAME)

# Create build directories
$(BUILD_DIR):
	mkdir -p $(BUILD_DIR)

# Assemble native code
$(NATIVE_OBJ): $(NATIVE_SRC) | $(BUILD_DIR)
	@echo "NASM $<"
	$(NASM) $(NASM_FLAGS) $< -o $@ -l $(BUILD_DIR)/native.lst

native: $(NATIVE_OBJ)

# Assemble bootloader
$(BOOTLOADER_OBJ): $(BOOTLOADER_SRC) | $(BUILD_DIR)
	@echo "NASM $(BOOTLOADER_SRC)"
	$(NASM) $(NASM_FLAGS) $< -o $@ -l $(BUILD_DIR)/boot.lst

# Link bootloader
$(BOOTLOADER_EFI): $(BOOTLOADER_OBJ)
	@echo "LINK $@"
	$(LD) -subsystem:efi_application -entry:EfiMain -out:$@ $<

bootloader: $(BOOTLOADER_EFI)

# Compile kernel (korlib + kernel C# sources together)
$(KERNEL_OBJ): $(KORLIB_SRC) $(KERNEL_SRC) | $(BUILD_DIR)
	@echo "BFLAT kernel"
	rm -rf $(KORLIB_DIR)/obj $(KORLIB_DIR)/bin
	$(BFLAT) build $(BFLAT_FLAGS) -c -o $@ $(KORLIB_SRC) $(KERNEL_SRC)

kernel: $(KERNEL_OBJ)

# Build JITTest assembly (comprehensive IL opcode tests)
JITTEST_SRC := $(call rwildcard,$(JITTEST_DIR),*.cs)
$(JITTEST_DLL): $(JITTEST_SRC) $(JITTEST_DIR)/JITTest.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build JITTest"
	dotnet build $(JITTEST_DIR)/JITTest.csproj -c Release -o $(BUILD_DIR) --nologo -v q

jittest: $(JITTEST_DLL)

# Build korlib IL assembly (for JIT generic instantiation)
$(KORLIB_DLL): $(KORLIB_SRC) $(KORLIB_DIR)/korlib.csproj | $(BUILD_DIR)
	@echo "DOTNET build korlib (IL assembly)"
	dotnet build $(KORLIB_DIR)/korlib.csproj -c Release -o $(BUILD_DIR) --nologo -v q

korlibdll: $(KORLIB_DLL)

# Build TestSupport library (cross-assembly test helpers)
TESTSUPPORT_SRC := $(call rwildcard,$(TESTSUPPORT_DIR),*.cs)
$(TESTSUPPORT_DLL): $(TESTSUPPORT_SRC) $(TESTSUPPORT_DIR)/TestSupport.csproj | $(BUILD_DIR)
	@echo "DOTNET build TestSupport"
	dotnet build $(TESTSUPPORT_DIR)/TestSupport.csproj -c Release -o $(BUILD_DIR) --nologo -v q

testsupport: $(TESTSUPPORT_DLL)

# Build DDK library (JIT-compiled at runtime)
DDK_SRC := $(call rwildcard,$(DDK_DIR),*.cs)
$(DDK_DLL): $(DDK_SRC) $(DDK_DIR)/DDK.csproj | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.DDK"
	dotnet build $(DDK_DIR)/DDK.csproj -c Release -o $(BUILD_DIR) --nologo -v q

ddk: $(DDK_DLL)

# Build ProtonOS.Net library (application-level networking)
PROTONOS_NET_SRC := $(call rwildcard,$(PROTONOS_NET_DIR),*.cs)
$(PROTONOS_NET_DLL): $(PROTONOS_NET_SRC) $(PROTONOS_NET_DIR)/ProtonOS.Net.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Net"
	dotnet build $(PROTONOS_NET_DIR)/ProtonOS.Net.csproj -c Release -o $(BUILD_DIR) --nologo -v q

protonos-net: $(PROTONOS_NET_DLL)

# Build AppTest assembly (application-level tests)
APPTEST_SRC := $(call rwildcard,$(APPTEST_DIR),*.cs)
$(APPTEST_DLL): $(APPTEST_SRC) $(APPTEST_DIR)/AppTest.csproj $(DDK_DLL) $(PROTONOS_NET_DLL) | $(BUILD_DIR)
	@echo "DOTNET build AppTest"
	dotnet build $(APPTEST_DIR)/AppTest.csproj -c Release -o $(BUILD_DIR) --nologo -v q

apptest: $(APPTEST_DLL)

# Build HelloApp (simple test app for execve - no args)
HELLOAPP_SRC := $(call rwildcard,$(HELLOAPP_DIR),*.cs)
$(HELLOAPP_DLL): $(HELLOAPP_SRC) $(HELLOAPP_DIR)/HelloApp.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build HelloApp"
	dotnet build $(HELLOAPP_DIR)/HelloApp.csproj -c Release -o $(BUILD_DIR) --nologo -v q

helloapp: $(HELLOAPP_DLL)

# Build ArgsApp (test app for execve with args)
ARGSAPP_SRC := $(call rwildcard,$(ARGSAPP_DIR),*.cs)
$(ARGSAPP_DLL): $(ARGSAPP_SRC) $(ARGSAPP_DIR)/ArgsApp.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ArgsApp"
	dotnet build $(ARGSAPP_DIR)/ArgsApp.csproj -c Release -o $(BUILD_DIR) --nologo -v q

argsapp: $(ARGSAPP_DLL)

# Build console_io_test.dll (Phase 2 console I/O acceptance test)
CONSOLETEST_SRC := $(call rwildcard,$(CONSOLETEST_DIR),*.cs)
$(CONSOLETEST_DLL): $(CONSOLETEST_SRC) $(CONSOLETEST_DIR)/ConsoleIoTest.csproj | $(BUILD_DIR)
	@echo "DOTNET build console_io_test"
	dotnet build $(CONSOLETEST_DIR)/ConsoleIoTest.csproj -c Release -o $(BUILD_DIR) --nologo -v q

consoletest: $(CONSOLETEST_DLL)

# Build Virtio common library
VIRTIO_SRC := $(call rwildcard,$(VIRTIO_DIR),*.cs)
$(VIRTIO_DLL): $(VIRTIO_SRC) $(VIRTIO_DIR)/Virtio.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.Virtio"
	dotnet build $(VIRTIO_DIR)/Virtio.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# Build Virtio-blk driver
VIRTIO_BLK_SRC := $(call rwildcard,$(VIRTIO_BLK_DIR),*.cs)
$(VIRTIO_BLK_DLL): $(VIRTIO_BLK_SRC) $(VIRTIO_BLK_DIR)/VirtioBlk.csproj $(VIRTIO_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.VirtioBlk"
	dotnet build $(VIRTIO_BLK_DIR)/VirtioBlk.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# Build Virtio-net driver
VIRTIO_NET_SRC := $(call rwildcard,$(VIRTIO_NET_DIR),*.cs)
$(VIRTIO_NET_DLL): $(VIRTIO_NET_SRC) $(VIRTIO_NET_DIR)/VirtioNet.csproj $(VIRTIO_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.VirtioNet"
	dotnet build $(VIRTIO_NET_DIR)/VirtioNet.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# Build FAT filesystem driver
FAT_SRC := $(call rwildcard,$(FAT_DIR),*.cs)
$(FAT_DLL): $(FAT_SRC) $(FAT_DIR)/Fat.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.Fat"
	dotnet build $(FAT_DIR)/Fat.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# Build AHCI driver
AHCI_SRC := $(call rwildcard,$(AHCI_DIR),*.cs)
$(AHCI_DLL): $(AHCI_SRC) $(AHCI_DIR)/Ahci.csproj $(DDK_DLL) $(FAT_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.Ahci"
	dotnet build $(AHCI_DIR)/Ahci.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# EXT2 filesystem driver
EXT2_SRC := $(call rwildcard,$(EXT2_DIR),*.cs)
$(EXT2_DLL): $(EXT2_SRC) $(EXT2_DIR)/Ext2.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.Ext2"
	dotnet build $(EXT2_DIR)/Ext2.csproj -c Release -o $(BUILD_DIR) --nologo -v q

# Test driver (loaded dynamically from /drivers on root filesystem)
TEST_DRIVER_SRC := $(call rwildcard,$(TEST_DRIVER_DIR),*.cs)
$(TEST_DRIVER_DLL): $(TEST_DRIVER_SRC) $(TEST_DRIVER_DIR)/TestDriver.csproj $(DDK_DLL) | $(BUILD_DIR)
	@echo "DOTNET build ProtonOS.Drivers.Test"
	dotnet build $(TEST_DRIVER_DIR)/TestDriver.csproj -c Release -o $(BUILD_DIR) --nologo -v q

drivers: $(VIRTIO_DLL) $(VIRTIO_BLK_DLL) $(VIRTIO_NET_DLL) $(FAT_DLL) $(AHCI_DLL) $(EXT2_DLL) $(TEST_DRIVER_DLL)

# Link UEFI executable with debug symbols
$(BUILD_DIR)/$(EFI_NAME): $(NATIVE_OBJ) $(KERNEL_OBJ)
	@echo "LINK $@"
	$(LD) $(LD_FLAGS) -debug -out:$@ $^
	@file $@
	@echo "Generating GDB-compatible symbols from PDB..."
	@python3 tools/gen_elf_syms.py $(BUILD_DIR)/BOOTX64.pdb $(BUILD_DIR)/kernel_syms.elf

# Create boot image
image: $(BUILD_DIR)/$(EFI_NAME) $(BOOTLOADER_EFI) $(JITTEST_DLL) $(KORLIB_DLL) $(TESTSUPPORT_DLL) $(DDK_DLL) $(PROTONOS_NET_DLL) $(APPTEST_DLL) $(HELLOAPP_DLL) $(ARGSAPP_DLL) $(CONSOLETEST_DLL) $(VIRTIO_DLL) $(VIRTIO_BLK_DLL) $(VIRTIO_NET_DLL) $(FAT_DLL) $(AHCI_DLL) $(EXT2_DLL) $(TEST_DRIVER_DLL)
	@echo "Creating boot image..."
	dd if=/dev/zero of=$(IMG) bs=1M count=64 status=none
	mformat -i $(IMG) -F -v NEUTRINOOS ::
	mmd -i $(IMG) ::/EFI
	mmd -i $(IMG) ::/EFI/BOOT
	mmd -i $(IMG) ::/drivers
	mmd -i $(IMG) ::/lib
	mcopy -i $(IMG) $(BOOTLOADER_EFI) ::/EFI/BOOT/$(EFI_NAME)
	mcopy -i $(IMG) $(BUILD_DIR)/BOOTX64.EFI ::/EFI/BOOT/$(KERNEL_NAME)
	mcopy -i $(IMG) $(JITTEST_DLL) ::/JITTest.dll
	mcopy -i $(IMG) $(KORLIB_DLL) ::/korlib.dll
	mcopy -i $(IMG) $(TESTSUPPORT_DLL) ::/TestSupport.dll
	mcopy -i $(IMG) $(DDK_DLL) ::/ProtonOS.DDK.dll
	mcopy -i $(IMG) $(APPTEST_DLL) ::/AppTest.dll
	mcopy -i $(IMG) $(HELLOAPP_DLL) ::/HelloApp.dll
	mcopy -i $(IMG) $(ARGSAPP_DLL) ::/ArgsApp.dll
	mcopy -i $(IMG) $(CONSOLETEST_DLL) ::/console_io_test.dll
	mcopy -i $(IMG) $(VIRTIO_DLL) ::/drivers/
	mcopy -i $(IMG) $(VIRTIO_BLK_DLL) ::/drivers/
	mcopy -i $(IMG) $(VIRTIO_NET_DLL) ::/drivers/
	mcopy -i $(IMG) $(FAT_DLL) ::/drivers/
	mcopy -i $(IMG) $(AHCI_DLL) ::/drivers/
	mcopy -i $(IMG) $(EXT2_DLL) ::/drivers/
	mcopy -i $(IMG) $(PROTONOS_NET_DLL) ::/lib/
	@echo "Boot image: $(IMG)"
	@echo "Contents:"
	@mdir -i $(IMG) ::/

clean:
	rm -rf build/

# Toolchain directories
RUNTIME_DIR := tools/runtime
BFLAT_DIR := tools/bflat
NUGET_LOCAL := tools/nuget-local
ILC_VERSION := 10.0.0-local.2

# Install system dependencies (requires sudo)
install-deps:
	@tools/install-deps.sh

# Build bflat toolchain (runtime + bflat)
# Run after install-deps, or when runtime/bflat changes
deps: check-deps
	@echo "Building runtime..."
	cd $(RUNTIME_DIR) && TreatWarningsAsErrors=false ./build.sh \
		clr.nativeaotlibs+clr.nativeaotruntime+clr.alljits+clr.tools \
		-c Release -arch x64 /p:GenerateDocumentationFile=false
	@echo "Packing ILCompiler..."
	cd $(RUNTIME_DIR) && ./dotnet.sh pack bflat/pack/ILCompiler.Compiler.nuproj \
		-p:Version=$(ILC_VERSION) \
		-p:IntermediateOutputPath=$(CURDIR)/$(RUNTIME_DIR)/artifacts/bin/coreclr/linux.x64.Release/ilc/
	@mkdir -p $(NUGET_LOCAL)
	cp $(RUNTIME_DIR)/artifacts/packages/Release/Shipping/BFlat.Compiler.$(ILC_VERSION).nupkg $(NUGET_LOCAL)/
	@echo "Building bflat..."
	cd $(BFLAT_DIR) && dotnet build src/bflat -c Release
	@echo "Dependencies built successfully."

# Quick dependency check (no install, just verify)
check-deps:
	@echo "Checking dependencies..."
	@command -v dotnet >/dev/null || (echo "ERROR: dotnet not found. Run 'make install-deps'" && exit 1)
	@dotnet --list-sdks | grep -q "^10\." || (echo "ERROR: .NET SDK 10.x not found. Run 'make install-deps'" && exit 1)
	@command -v clang >/dev/null || (echo "ERROR: clang not found. Run 'make install-deps'" && exit 1)
	@command -v cmake >/dev/null || (echo "ERROR: cmake not found. Run 'make install-deps'" && exit 1)
	@command -v nasm >/dev/null || (echo "ERROR: nasm not found. Run 'make install-deps'" && exit 1)
	@echo "All dependencies found."

# Run in QEMU (full test environment: boot + test + sata disks)
run: image
	./run.sh

# Run in QEMU with the minimal serial-only Phase 1 configuration:
# OVMF (pflash) + neutrinoos.img on virtio, no graphics, serial on stdio
run-qemu: image
	./tools/run-qemu.sh

# Phase 2: boot with the serial console attached to the terminal.
# Interactive shell (neutrinoos>) with echo, editing, history, colors.
# Quit QEMU with Ctrl+A X.
run-qemu-serial: image
	@test -f $(OVMF_VARS) || cp $(OVMF_VARS_SRC) $(OVMF_VARS)
	@echo "NeutrinoOS serial console (Ctrl+A X quits QEMU)"
	qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
		-drive if=pflash,format=raw,readonly=on,file=$(OVMF_CODE) \
		-drive if=pflash,format=raw,file=$(OVMF_VARS) \
		-drive file=$(IMG),format=raw,if=virtio \
		-display none -serial stdio -no-reboot -no-shutdown

# Phase 2: same as run-qemu-serial, but also tee the serial stream to
# $(BUILD_DIR)/serial.log for inspection from Windows.
run-qemu-serial-log: image
	@test -f $(OVMF_VARS) || cp $(OVMF_VARS_SRC) $(OVMF_VARS)
	@echo "NeutrinoOS serial console -> $(BUILD_DIR)/serial.log (Ctrl+A X quits QEMU)"
	qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
		-drive if=pflash,format=raw,readonly=on,file=$(OVMF_CODE) \
		-drive if=pflash,format=raw,file=$(OVMF_VARS) \
		-drive file=$(IMG),format=raw,if=virtio \
		-display none -serial stdio -no-reboot -no-shutdown 2>&1 | tee $(BUILD_DIR)/serial.log

# Run in VirtualBox: converts the image to .vdi and shows the UEFI VM
# configuration with the serial port redirected to a host file
# (requires VBoxManage on PATH; see docs/BUILD-WINDOWS.md)
run-vbox: image
	./tools/run-vbox.sh

# Show configuration
info:
	@echo "ARCH:       $(ARCH)"
	@echo "BUILD_DIR:  $(BUILD_DIR)"
	@echo "NATIVE_SRC: $(NATIVE_SRC)"
	@echo "KORLIB_SRC: $(words $(KORLIB_SRC)) files"
	@echo "KERNEL_SRC: $(words $(KERNEL_SRC)) files"
	@echo "EFI_NAME:   $(EFI_NAME)"
