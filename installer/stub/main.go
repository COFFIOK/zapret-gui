// Zapret Setup — заглушка установщика · by COFFIOK
// Скрипт установщика (setup.ps1) дописан в конец этого exe командой build-release.cmd:
//   [exe-заглушка][setup.ps1][8 байт: длина скрипта, little-endian]["ZSETUP01"]
// Заглушка достаёт скрипт во временный файл и запускает его через PowerShell.
// Пересобирать заглушку нужно только при изменении этого файла (см. installer\stub\build-stub.sh).
package main

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

const magic = "ZSETUP01"

func fail(msg string) {
	logPath := filepath.Join(os.TempDir(), "zapret-setup-error.log")
	_ = os.WriteFile(logPath, []byte(time.Now().Format("2006-01-02 15:04:05")+" "+msg+"\r\n"), 0o644)
	os.Exit(1)
}

func readScript() []byte {
	self, err := os.Executable()
	if err != nil {
		fail("exe path: " + err.Error())
	}
	data, err := os.ReadFile(self)
	if err != nil {
		fail("read exe: " + err.Error())
	}
	if len(data) < 16 || !bytes.Equal(data[len(data)-8:], []byte(magic)) {
		fail("в установщике нет скрипта (собери его через build-release.cmd)")
	}
	n := binary.LittleEndian.Uint64(data[len(data)-16 : len(data)-8])
	if n == 0 || n > uint64(len(data)-16) {
		fail("повреждённый установщик")
	}
	return data[len(data)-16-int(n) : len(data)-16]
}

// удаляем старые копии скрипта, оставшиеся после прошлых запусков
func cleanupOld(dir string) {
	old, _ := filepath.Glob(filepath.Join(dir, "zapret-setup-*.ps1"))
	for _, f := range old {
		if st, err := os.Stat(f); err == nil && time.Since(st.ModTime()) > 10*time.Minute {
			_ = os.Remove(f)
		}
	}
}

func main() {
	script := readScript()
	// системная папка Temp: обычная программа пользователя не сможет подменить скрипт до запуска
	dir := os.TempDir()
	if sr := os.Getenv("SystemRoot"); sr != "" {
		if st, err := os.Stat(filepath.Join(sr, "Temp")); err == nil && st.IsDir() {
			dir = filepath.Join(sr, "Temp")
		}
	}
	cleanupOld(dir)
	path := filepath.Join(dir, fmt.Sprintf("zapret-setup-%d.ps1", time.Now().UnixNano()))
	if err := os.WriteFile(path, script, 0o600); err != nil {
		fail("write script: " + err.Error())
	}
	defer os.Remove(path)

	ps := filepath.Join(os.Getenv("SystemRoot"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
	if os.Getenv("SystemRoot") == "" {
		ps = "powershell.exe"
	}
	args := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", path, "-SelfPath", path}
	for _, a := range os.Args[1:] {
		if strings.EqualFold(a, "-Auto") || strings.EqualFold(a, "/auto") || strings.EqualFold(a, "--auto") {
			args = append(args, "-Auto")
		}
	}
	cmd := exec.Command(ps, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000} // CREATE_NO_WINDOW
	if err := cmd.Run(); err != nil {
		if _, ok := err.(*exec.ExitError); !ok {
			fail("powershell: " + err.Error())
		}
	}
}
