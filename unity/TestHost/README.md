# TestHost

A minimal Unity project whose only job is to run the engine package's
EditMode tests — locally (headless) and in CI. The `testables` entry in
`Packages/manifest.json` is what makes Unity pick up `com.lvn.engine`'s
test assemblies.

    Unity -batchmode -projectPath unity/TestHost -runTests -testPlatform EditMode
