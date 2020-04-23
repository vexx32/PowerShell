Describe 'Function Pipeline Behaviour' -Tag 'CI' {

    Context 'Output from Named Blocks' {

        $Cases = @(
            @{ Script = { begin { 10 } }; ExpectedResult = 10 }
            @{ Script = { process { 15 } }; ExpectedResult = 15 }
            @{ Script = { end { 22 } }; ExpectedResult = 22 }
        )
        It 'permits output from named block: <Script>' -TestCases $Cases {
            param($Script, $ExpectedResult)

            & $Script | Should -Be $ExpectedResult
        }

        It 'does not permit output from dispose{}' {
            & { dispose { 11 } } | Should -BeNullOrEmpty
        }

        It 'passes output for begin, then process, then end' {
            $Script = {
                process { "PROCESS" }
                begin { "BEGIN" }
                end { "END" }
            }

            & $Script | Should -Be @( "BEGIN", "PROCESS", "END" )
        }

    }

    Context 'Output Behaviour on Error States' {

        It 'does not execute End {} if the pipeline is halted during Process {}' {
            # We don't need Should -Not -Throw as if this reaches end{} and throws the test will fail anyway.
            1..10 |
                & {
                    begin { "BEGIN" }
                    process { "PROCESS $_" }
                    end { "END"; throw "This should not be reached." }
                } |
                Select-Object -First 3 |
                Should -Be @( "BEGIN", "PROCESS 1", "PROCESS 2" )
        }

        It 'still executes Dispose {} if the pipeline is halted' {
            $Script = {
                1..10 |
                    & {
                        process { $_ }
                        dispose { throw "Dispose block hit." }
                } |
                    Select-Object -First 1
            }
            $Script | Should -Throw -ExpectedMessage "Dispose block hit."
        }
    }

    AfterAll {

    }
}
