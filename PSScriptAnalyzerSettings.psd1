# KofTwentyTwo PowerShell analysis (K22-CODE-SH-05), from KofTwentyTwo/standards
# configs/shell/PSScriptAnalyzerSettings.psd1. CI runs:
#   Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -EnableExit
# and `Invoke-Formatter` with the same settings for layout. PowerShell supports the
# Kingsrook layout fully: next-line (Allman) braces and 3-space indentation.
@{
   Severity     = @('Error', 'Warning', 'Information')
   IncludeDefaultRules = $true
   ExcludeRules = @()
   Rules        = @{
      PSPlaceOpenBrace           = @{
         Enable             = $true
         OnSameLine         = $false
         NewLineAfter       = $true
         IgnoreOneLineBlock = $true
      }
      PSPlaceCloseBrace          = @{
         Enable             = $true
         NewLineAfter       = $true
         IgnoreOneLineBlock = $true
         NoEmptyLineBefore  = $true
      }
      PSUseConsistentIndentation = @{
         Enable              = $true
         IndentationSize     = 3
         Kind                = 'space'
         PipelineIndentation = 'IncreaseIndentationForFirstPipeline'
      }
      PSUseConsistentWhitespace  = @{
         Enable                          = $true
         CheckInnerBrace                 = $true
         # Kingsrook writes if(...) with no space; the rule cannot require that, so it
         # is not checked either way.
         CheckOpenParen                  = $false
         CheckOpenBrace                  = $false
         CheckOperator                   = $true
         CheckPipe                       = $true
         CheckSeparator                  = $true
         CheckParameter                  = $true
      }
      PSAlignAssignmentStatement = @{
         Enable         = $true
         CheckHashtable = $true
      }
      PSUseCorrectCasing         = @{ Enable = $true }
      PSAvoidUsingCmdletAliases  = @{ Enable = $true }
      PSAvoidUsingWriteHost      = @{ Enable = $true }
      PSProvideCommentHelp       = @{
         Enable                  = $true
         ExportedOnly            = $false
         BlockComment            = $true
         Placement               = 'before'
      }
   }
}
