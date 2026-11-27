using System.Text.Json;

namespace VMCrate.Windows;

internal static class DeepSeekProfile
{
    internal const string Ready = "VMCRATE_TOOL_GUARD_ACTIVE";

    internal static string Write(string directory)
    {
        var guard = Path.Combine(directory, "text-only.mjs");
        File.WriteAllText(guard, $$"""
            export const name = 'vmcrate-text-only';
            export const inject = ['tools'];
            export function apply(ctx) {
                ctx.tools.guard(() => 'VMCrate accepts text responses only.');
                ctx.provide('vmcrateTextOnly', true);
                process.stderr.write('{{Ready}}\n');
            }
            """);
        var patch = Path.Combine(directory, "assistant.yml");
        File.WriteAllText(patch, $$"""
            - id: credentials
              disabled: false
            - id: authorization
              disabled: false
            - id: deepseek-account
              disabled: false
            - id: agent-default-model
              config:
                provider: deepseek-account
                model: deepseek-flash
            - id: llm-deepseek
              disabled: true
            - id: llm-pi-ai
              disabled: true
            - id: tool-executor-ptc
              disabled: true
            - id: tool-web
              disabled: true
            - id: session-log-deepseek
              config:
                enabled: false
            - insert:
                - id: vmcrate-text-only
                  name: {{JsonSerializer.Serialize(guard)}}
            - id: headless-runner
              inject: [headlessStartup, vmcrateTextOnly]
            """);
        return patch;
    }
}
