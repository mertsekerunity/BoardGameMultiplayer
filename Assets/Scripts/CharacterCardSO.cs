using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "Character Card", menuName = "Cards/Character Card", order = 1)]
public class CharacterCardSO : ScriptableObject
{
    public int characterNumber;                // 1–9
    public string characterName;               // e.g. “Thief”
    public Sprite characterSprite;             // card sprite
    public CharacterAbilityType abilityType;   // enum if you like any parameters for the ability, e.g. stealPercentage, extraMoveCount, etc.
    public CharacterAbilityType secondaryAbilityType;
}
