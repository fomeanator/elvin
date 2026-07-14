package main

// docs.go — the API's own documentation, shipped inside the binary: the
// OpenAPI spec (openapi.yaml, embedded at build time) at /openapi.yaml and
// an interactive Swagger UI at /docs. An engine adopter's server documents
// itself — no separate docs hosting to set up or drift out of date.

import (
	_ "embed"
	"net/http"
)

//go:embed openapi.yaml
var openapiSpec []byte

// docsHTML loads Swagger UI from the unpkg CDN — the one external dependency,
// acceptable for an admin-facing page (the spec itself stays self-hosted; any
// OpenAPI viewer can read /openapi.yaml offline).
const docsHTML = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>LVN Server API</title>
  <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"/>
  <style>body{margin:0}</style>
</head>
<body>
  <div id="ui"></div>
  <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
  <script>
    SwaggerUIBundle({
      url: "/openapi.yaml",
      dom_id: "#ui",
      deepLinking: true,
      tryItOutEnabled: true,
      persistAuthorization: true,
    });
  </script>
</body>
</html>`

func (s *server) routesDocs(mux *http.ServeMux) {
	mux.HandleFunc("/openapi.yaml", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/yaml; charset=utf-8")
		w.Write(openapiSpec)
	})
	mux.HandleFunc("/docs", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Write([]byte(docsHTML))
	})
}
