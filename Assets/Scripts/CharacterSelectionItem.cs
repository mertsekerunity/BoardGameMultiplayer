using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

public class CharacterSelectionItem : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private TextMeshProUGUI label;

    private CharacterCardSO _card;

    public void Bind(CharacterCardSO card, Action onClick)
    {
        _card = card;
        if (label) label.text = $"{(int)card.characterNumber} - {card.characterName}";

        button.onClick.RemoveAllListeners();

        button.onClick.AddListener(() =>
        {
            onClick?.Invoke();
        });
    }

    public void SetInteractable(bool on)
    {
        button.interactable = on;
        var cg = GetComponent<CanvasGroup>();
        if (cg) cg.alpha = on ? 1f : 0f;
    }
}
