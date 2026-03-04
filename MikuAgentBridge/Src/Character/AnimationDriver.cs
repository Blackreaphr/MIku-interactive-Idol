using UnityEngine;

namespace MikuAgentBridge.Character;

public sealed class AnimationDriver
{
    private static readonly string[] PushCandidates = ["Push", "Hop", "Wave"];

    public bool TryTriggerPush(Animator? animator)
    {
        return TryTrigger(animator, PushCandidates);
    }

    private static bool TryTrigger(Animator? animator, IReadOnlyCollection<string> candidates)
    {
        if (animator == null)
        {
            return false;
        }

        AnimatorControllerParameter[] parameters;
        try
        {
            parameters = animator.parameters;
        }
        catch
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.type != AnimatorControllerParameterType.Trigger)
                {
                    continue;
                }

                if (!string.Equals(parameter.name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    animator.SetTrigger(parameter.name);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        return false;
    }
}
