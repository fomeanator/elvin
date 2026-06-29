module github.com/fomeanator/unity-lvn-vn-engine/server

go 1.25.0

require github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv v0.0.0

require (
	golang.org/x/image v0.43.0 // indirect
	golang.org/x/text v0.38.0 // indirect
)

replace github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv => ../tools/lvnconv
