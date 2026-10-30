package main

import (
	_ "embed"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"time"
)

//go:embed setup.ps1
var script []byte

func main() {
	p := filepath.Join(os.TempDir(), fmt.Sprintf("zapret-setup-%d.ps1", time.Now().UnixNano()))
	if err := os.WriteFile(p, script, 0644); err != nil {
		return
	}
	defer os.Remove(p)
	args := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", p, "-SelfPath", p}
	for _, a := range os.Args[1:] {
		if a == "-Auto" || a == "/auto" || a == "--auto" {
			args = append(args, "-Auto")
		}
	}
	cmd := exec.Command("powershell.exe", args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
	_ = cmd.Run()
}
