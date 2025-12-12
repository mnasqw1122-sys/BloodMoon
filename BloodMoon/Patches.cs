using HarmonyLib;
using UnityEngine;
using Duckov;
using Duckov.Scenes;
using Duckov.CreditsUtility;
using System;
using System.Reflection;

namespace BloodMoon
{
    public static class Patches
    {
        // Apply all patches
        public static void ManualPatch(Harmony harmony)
        {
            // 1. Fix Animation System crashes (Critical for Bosses)
            PatchMagicBlend(harmony);
            
            // 2. Fix Credits Parsing Bug (General Stability)
            // Use PatchAll for attribute-based patches (CreditsLexer & LevelManager)
            harmony.PatchAll(typeof(Patches).Assembly);
        }

        private static void PatchMagicBlend(Harmony harmony)
        {
            try
            {
                // Patching KINEMATION.MagicBlend types dynamically as they might not be referenced
                Type magicBlending = AccessTools.TypeByName("KINEMATION.MagicBlend.Runtime.MagicBlending");
                if (magicBlending != null)
                {
                    MethodInfo updateAsset = AccessTools.Method(magicBlending, "UpdateMagicBlendAsset");
                    if (updateAsset != null)
                    {
                        var patches = Harmony.GetPatchInfo(updateAsset);
                        if (patches == null || patches.Finalizers.Count == 0)
                        {
                            harmony.Patch(updateAsset, finalizer: new HarmonyMethod(typeof(Patches), nameof(GenericFinalizer)));
                        }
                    }
                }

                Type magicState = AccessTools.TypeByName("KINEMATION.MagicBlend.Runtime.MagicBlendState");
                if (magicState != null)
                {
                    MethodInfo onStateEnter = AccessTools.Method(magicState, "OnStateEnter", new Type[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) });
                    if (onStateEnter != null)
                    {
                        // Check for ambiguity and existing patches
                        var patches = Harmony.GetPatchInfo(onStateEnter);
                        if (patches == null || patches.Finalizers.Count == 0)
                        {
                            harmony.Patch(onStateEnter, finalizer: new HarmonyMethod(typeof(Patches), nameof(GenericFinalizer)));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BloodMoon] PatchMagicBlend failed: {e}");
            }
        }

        

        // Generic finalizer to suppress exceptions in external libraries
        public static Exception? GenericFinalizer(Exception __exception)
        {
            return null; // Suppress exception to prevent crash
        }

        // --- Harmony Patches ---

        [HarmonyPatch(typeof(CreditsLexer), "Next")]
        public static class CreditsLexer_Next_Patch
        {
            // Optimized: Use ref injection (___content, ___cursor) instead of slow Reflection
            public static bool Prefix(CreditsLexer __instance, ref Token __result, ref string ___content, ref ushort ___cursor)
            {
                try
                {
                    string content = ___content;
                    int cursor = ___cursor;

                    // Skip whitespace
                    while (cursor < content.Length)
                    {
                        char c = content[cursor];
                        if (!char.IsWhiteSpace(c) || c == '\n') break;
                        cursor++;
                    }

                    if (cursor >= content.Length)
                    {
                        ___cursor = (ushort)(++cursor);
                        __result = new Token(TokenType.End);
                        return false;
                    }

                    char currentChar = content[cursor];

                    if (currentChar == '\n')
                    {
                        ___cursor = (ushort)(++cursor);
                        __result = new Token(TokenType.EmptyLine);
                        return false;
                    }
                    
                    if (currentChar == '#')
                    {
                        cursor++;
                        int startIndex = cursor;
                        while (cursor < content.Length && content[cursor] != '\n')
                        {
                            cursor++;
                        }
                        
                        // FIX: Original code used content.Substring(startIndex, cursor), causing out of bounds
                        string commentText = cursor > startIndex ? content.Substring(startIndex, cursor - startIndex) : "";
                        
                        ___cursor = (ushort)(++cursor);
                        __result = new Token(TokenType.Comment, commentText);
                        return false;
                    }

                    if (currentChar == '[')
                    {
                        cursor++;
                        int start = cursor;
                        while (cursor < content.Length)
                        {
                            if (content[cursor] == ']')
                            {
                                string text = content.Substring(start, cursor - start);
                                while (cursor < content.Length)
                                {
                                    cursor++;
                                    if (cursor >= content.Length) break;
                                    char c = content[cursor];
                                    if (c == '\n') { cursor++; break; }
                                    if (!char.IsWhiteSpace(c)) break;
                                }
                                ___cursor = (ushort)cursor;
                                __result = new Token(TokenType.Instructor, text);
                                return false;
                            }
                            if (content[cursor] == '\n')
                            {
                                int len = cursor - (start - 1);
                                string invalidText = len > 0 ? content.Substring(start - 1, len) : "[";
                                ___cursor = (ushort)(++cursor);
                                __result = new Token(TokenType.Invalid, invalidText);
                                return false;
                            }
                            cursor++;
                        }
                        ___cursor = (ushort)cursor;
                        __result = new Token(TokenType.Invalid, content.Substring(start - 1));
                        return false;
                    }

                    // Default (String)
                    int strStart = cursor;
                    string raw;
                    while (cursor < content.Length)
                    {
                        char c = content[cursor];
                        if (c == '\n')
                        {
                            raw = content.Substring(strStart, cursor - strStart);
                            ___cursor = (ushort)(++cursor);
                            __result = new Token(TokenType.String, raw.Replace("\\n", "\n"));
                            return false;
                        }
                        if (c == '#')
                        {
                            raw = content.Substring(strStart, cursor - strStart);
                            ___cursor = (ushort)cursor;
                            __result = new Token(TokenType.String, raw.Replace("\\n", "\n"));
                            return false;
                        }
                        cursor++;
                    }
                    raw = content.Substring(strStart, cursor - strStart);
                    ___cursor = (ushort)cursor;
                    __result = new Token(TokenType.String, raw.Replace("\\n", "\n"));
                    return false;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BloodMoon] CreditsLexer Patch Error: {e}");
                    return true; // Fallback to original
                }
            }
        }

        [HarmonyPatch(typeof(LevelManager), "IsPathCompatible")]
        public static class LevelManager_IsPathCompatible_Patch
        {
            public static bool Prefix(ref bool __result, SubSceneEntry.Location location, string keyWord)
            {
                try
                {
                    if (location == null || string.IsNullOrEmpty(location.path)) 
                    { 
                        __result = false; 
                        return false; 
                    }

                    string path = location.path;
                    int num = path.IndexOf('/');
                    if (num != -1)
                    {
                        if (num > path.Length) num = path.Length; 
                        string sub = path.Substring(0, num);
                        // Strict folder matching to prevent partial matches
                        if (sub == keyWord)
                        {
                            __result = true;
                            return false;
                        }
                    }
                    __result = false;
                    return false;
                }
                catch
                {
                    __result = false;
                    return false;
                }
            }
        }
    }
}
