@echo off
rem 16.11: new look of the administrator, cashier and publisher (all their jobs are regenerated) + 3D references of the teacher, patient and librarian.
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\generate.ps1" -Only "ref3d_admin_,ref3d_kassir_,ref3d_izdatel_,admin_desk,port_admin,kassir,port_kassir,publisher,izdatel_sit_read,port_izdatel" -Force
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\generate.ps1" -Only "ref3d_"
pause
