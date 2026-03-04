using UnityEngine;

namespace MikuAgentBridge.Character;

public sealed class CharacterLocator
{
    private const float ScanIntervalSeconds = 2.0f;

    private Animator? _cachedAnimator;
    private string? _cachedName;
    private float _nextScanAt;

    public bool CharacterFound => CurrentAnimator is not null;

    public string? CharacterName => CharacterFound ? _cachedName : null;

    public Animator? CurrentAnimator
    {
        get
        {
            if (_cachedAnimator == null)
            {
                return null;
            }

            return _cachedAnimator;
        }
    }

    public void Tick()
    {
        var now = Time.realtimeSinceStartup;

        if (_cachedAnimator != null && now < _nextScanAt)
        {
            return;
        }

        _nextScanAt = now + ScanIntervalSeconds;
        _cachedAnimator = FindBestAnimator();
        _cachedName = _cachedAnimator != null ? _cachedAnimator.gameObject.name : null;
    }

    private static Animator? FindBestAnimator()
    {
        var animators = UnityEngine.Object.FindObjectsOfType<Animator>(true);
        if (animators is null || animators.Length == 0)
        {
            return null;
        }

        Animator? best = null;
        var bestScore = int.MinValue;

        foreach (var animator in animators)
        {
            if (animator == null)
            {
                continue;
            }

            var score = Score(animator);
            if (score > bestScore)
            {
                best = animator;
                bestScore = score;
            }
        }

        return best;
    }

    private static int Score(Animator animator)
    {
        var score = 0;

        if (animator.avatar != null && animator.avatar.isHuman)
        {
            score += 8;
        }

        if (animator.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
        {
            score += 5;
        }

        if (animator.runtimeAnimatorController != null)
        {
            score += 2;
        }

        if (animator.isActiveAndEnabled)
        {
            score += 2;
        }

        var name = animator.gameObject.name;
        if (name.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("canvas", StringComparison.OrdinalIgnoreCase))
        {
            score -= 6;
        }

        if (animator.transform.lossyScale.magnitude < 0.1f)
        {
            score -= 2;
        }

        return score;
    }
}
