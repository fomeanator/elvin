module github.com/fomeanator/elvin/server

go 1.25.0

require (
	github.com/fomeanator/elvin/tools/lvnconv v0.0.0
	github.com/nwaples/rardecode v1.1.3
	golang.org/x/image v0.43.0
)

require golang.org/x/text v0.38.0 // indirect

replace github.com/fomeanator/elvin/tools/lvnconv => ../tools/lvnconv
