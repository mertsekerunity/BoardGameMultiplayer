using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class CharacterButton
{
    public int characterNumber;              
    public Button button;
    public TextMeshProUGUI label;            
}

public class CharacterTargetPanel : MonoBehaviour
{
    [SerializeField] private List<CharacterButton> buttons = new();

    private Action<int> _onPick;

    public void Show(HashSet<int> enabledNumbers, HashSet<int> disabledNumbers, Action<int> onPick)
    {
        _onPick = onPick;
        gameObject.SetActive(true);

        enabledNumbers ??= new HashSet<int>();
        disabledNumbers ??= new HashSet<int>();

        foreach (var cb in buttons)
        {
            bool enable = enabledNumbers.Contains(cb.characterNumber);
            bool disable = disabledNumbers.Contains(cb.characterNumber);

            if (cb.button == null) continue;

            cb.button.onClick.RemoveAllListeners();
            cb.button.interactable = enable && !disable;
            var captured = cb.characterNumber;
            cb.button.onClick.AddListener(() => Pick(captured));
        }
    }

    public void Hide()
    {
        gameObject.SetActive(false);
        _onPick = null;
    }

    private void Pick(int characterNumber)
    {
        _onPick?.Invoke(characterNumber);
        Hide();
    }
}
