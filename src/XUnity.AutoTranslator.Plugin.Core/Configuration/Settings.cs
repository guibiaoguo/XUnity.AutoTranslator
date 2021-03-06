﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XUnity.AutoTranslator.Plugin.Core.Constants;

namespace XUnity.AutoTranslator.Plugin.Core.Configuration
{
   public static class Settings
   {
      // cannot be changed
      public static readonly int MaxErrors = 5;
      public static readonly int MaxConcurrentTranslations = 5;
      public static readonly TimeSpan WebClientLifetime = TimeSpan.FromSeconds( 20 );
      
      // can be changed
      public static string ServiceEndpoint;
      public static string Language;
      public static string FromLanguage;
      public static string OutputFile;
      public static string TranslationDirectory;
      public static float Delay;
      public static int MaxCharactersPerTranslation;
      public static bool EnablePrintHierarchy;
      public static string AutoTranslationsFilePath;
      public static bool EnableIMGUI;
      public static bool EnableUGUI;
      public static bool EnableNGUI;
      public static bool EnableTextMeshPro;
      public static bool AllowPluginHookOverride;
      public static bool IgnoreWhitespaceInKeys;
      public static bool EnableSSL;

      public static void Configure()
      {
         ServiceEndpoint = Config.Current.Preferences[ "AutoTranslator" ][ "Endpoint" ].GetOrDefault( KnownEndpointNames.GoogleTranslate );
         Language = Config.Current.Preferences[ "AutoTranslator" ][ "Language" ].GetOrDefault( "en" );
         FromLanguage = Config.Current.Preferences[ "AutoTranslator" ][ "FromLanguage" ].GetOrDefault( "ja", true );
         Delay = Config.Current.Preferences[ "AutoTranslator" ][ "Delay" ].GetOrDefault( 0f );
         TranslationDirectory = Config.Current.Preferences[ "AutoTranslator" ][ "Directory" ].GetOrDefault( @"Translation" );
         OutputFile = Config.Current.Preferences[ "AutoTranslator" ][ "OutputFile" ].GetOrDefault( @"Translation\_AutoGeneratedTranslations.{lang}.txt" );
         MaxCharactersPerTranslation = Config.Current.Preferences[ "AutoTranslator" ][ "MaxCharactersPerTranslation" ].GetOrDefault( 150 );
         EnablePrintHierarchy = Config.Current.Preferences[ "AutoTranslator" ][ "EnablePrintHierarchy" ].GetOrDefault( false );
         IgnoreWhitespaceInKeys = Config.Current.Preferences[ "AutoTranslator" ][ "IgnoreWhitespaceInKeys" ].GetOrDefault( true );

         EnableIMGUI = Config.Current.Preferences[ "AutoTranslator" ][ "EnableIMGUI" ].GetOrDefault( true );
         EnableUGUI = Config.Current.Preferences[ "AutoTranslator" ][ "EnableUGUI" ].GetOrDefault( true );
         EnableNGUI = Config.Current.Preferences[ "AutoTranslator" ][ "EnableNGUI" ].GetOrDefault( true );
         EnableTextMeshPro = Config.Current.Preferences[ "AutoTranslator" ][ "EnableTextMeshPro" ].GetOrDefault( true );
         AllowPluginHookOverride = Config.Current.Preferences[ "AutoTranslator" ][ "AllowPluginHookOverride" ].GetOrDefault( true );

         EnableSSL = Config.Current.Preferences[ "AutoTranslator" ][ "EnableSSL" ].GetOrDefault( false );

         AutoTranslationsFilePath = Path.Combine( Config.Current.DataPath, OutputFile.Replace( "{lang}", Language ) );

         Config.Current.SaveConfig();
      }
   }
}
