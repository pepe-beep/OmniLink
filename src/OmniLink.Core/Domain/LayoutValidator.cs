using System.Collections.Generic;

namespace OmniLink.Domain;

public readonly record struct LayoutValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Domain Validator ตรวจสอบความถูกต้องของแผนผังการจัดวางหน้าจอ (Topology Validation)
/// </summary>
public static class LayoutValidator
{
    public static LayoutValidationResult Validate(LayoutModel layout)
    {
        var errors = new List<string>();

        if (layout == null || layout.Nodes.Count == 0)
        {
            errors.Add("Layout must contain at least one machine node.");
            return new LayoutValidationResult(false, errors);
        }

        var hostCount = 0;
        foreach (var (id, node) in layout.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                errors.Add($"Node key '{id}' has empty NodeId.");
            }

            if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0)
            {
                errors.Add($"Node '{node.NodeId}' has invalid dimensions ({node.Bounds.Width}x{node.Bounds.Height}).");
            }

            if (node.IsHost)
            {
                hostCount++;
            }

            // Verify links point to existing nodes
            if (!string.IsNullOrEmpty(node.LeftNodeId) && !layout.Nodes.ContainsKey(node.LeftNodeId))
            {
                errors.Add($"Node '{node.NodeId}' references non-existent LeftNodeId '{node.LeftNodeId}'.");
            }
            if (!string.IsNullOrEmpty(node.RightNodeId) && !layout.Nodes.ContainsKey(node.RightNodeId))
            {
                errors.Add($"Node '{node.NodeId}' references non-existent RightNodeId '{node.RightNodeId}'.");
            }
            if (!string.IsNullOrEmpty(node.UpNodeId) && !layout.Nodes.ContainsKey(node.UpNodeId))
            {
                errors.Add($"Node '{node.NodeId}' references non-existent UpNodeId '{node.UpNodeId}'.");
            }
            if (!string.IsNullOrEmpty(node.DownNodeId) && !layout.Nodes.ContainsKey(node.DownNodeId))
            {
                errors.Add($"Node '{node.NodeId}' references non-existent DownNodeId '{node.DownNodeId}'.");
            }
        }

        if (hostCount == 0)
        {
            errors.Add("Layout must contain at least one Host machine.");
        }

        return new LayoutValidationResult(errors.Count == 0, errors);
    }
}
