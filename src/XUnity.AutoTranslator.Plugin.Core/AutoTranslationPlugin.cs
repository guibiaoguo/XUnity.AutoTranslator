﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ExIni;
using UnityEngine;
using UnityEngine.UI;
using System.Globalization;
using XUnity.AutoTranslator.Plugin.Core.Extensions;
using UnityEngine.EventSystems;
using XUnity.AutoTranslator.Plugin.Core.Configuration;
using XUnity.AutoTranslator.Plugin.Core.Utilities;
using XUnity.AutoTranslator.Plugin.Core.Web;
using XUnity.AutoTranslator.Plugin.Core.Hooks;
using XUnity.AutoTranslator.Plugin.Core.Hooks.TextMeshPro;
using XUnity.AutoTranslator.Plugin.Core.Hooks.UGUI;
using XUnity.AutoTranslator.Plugin.Core.IMGUI;
using XUnity.AutoTranslator.Plugin.Core.Hooks.NGUI;
using UnityEngine.SceneManagement;
using XUnity.AutoTranslator.Plugin.Core.Constants;

namespace XUnity.AutoTranslator.Plugin.Core
{
   public class AutoTranslationPlugin : MonoBehaviour
   {
      private static readonly string TextPropertyName = "text";

      /// <summary>
      /// These are the currently running translation jobs (being translated by an http request).
      /// </summary>
      private List<TranslationJob> _completedJobs = new List<TranslationJob>();
      private List<TranslationJob> _unstartedJobs = new List<TranslationJob>();

      /// <summary>
      /// All the translations are stored in this dictionary.
      /// </summary>
      private Dictionary<string, string> _translations = new Dictionary<string, string>();

      /// <summary>
      /// These are the new translations that has not yet been persisted to the file system.
      /// </summary>
      private object _writeToFileSync = new object();
      private Dictionary<string, string> _newTranslations = new Dictionary<string, string>();
      private HashSet<string> _newUntranslated = new HashSet<string>();
      private HashSet<string> _translatedTexts = new HashSet<string>();

      /// <summary>
      /// The number of http translation errors that has occurred up until now.
      /// </summary>
      private int _consecutiveErrors = 0;

      /// <summary>
      /// This is a hash set that contains all Text components that is currently being worked on by
      /// the translation plugin.
      /// </summary>
      private HashSet<object> _ongoingOperations = new HashSet<object>();
      private HashSet<string> _startedOperationsForNonStabilizableComponents = new HashSet<string>();

      private bool _isInTranslatedMode = true;

      public void Initialize()
      {
         Settings.Configure();

         HooksSetup.InstallHooks( Any_TextChanged );

         AutoTranslateClient.Configure();

         LoadTranslations();

         // start a thread that will periodically removed unused references
         var t1 = new Thread( RemovedUnusedReferences );
         t1.IsBackground = true;
         t1.Start();

         // start a thread that will periodically save new translations
         var t2 = new Thread( SaveTranslationsLoop );
         t2.IsBackground = true;
         t2.Start();


         // subscribe to text changes
         UGUIHooks.TextAwakened += UguiTextEvents_OnTextAwaken;
         UGUIHooks.TextChanged += UguiTextEvents_OnTextChanged;
         IMGUIHooks.TextChanged += IMGUITextEvents_GUIContentChanged;
         NGUIHooks.TextChanged += NGUITextEvents_TextChanged;

         TextMeshProHooks.TextAwakened += TextMeshProHooks_OnTextAwaken;
         TextMeshProHooks.TextChanged += TextMeshProHooks_OnTextChanged;
      }

      private string[] GetTranslationFiles()
      {
         return Directory.GetFiles( Path.Combine( Config.Current.DataPath, Settings.TranslationDirectory ), $"*.txt", SearchOption.AllDirectories ) // FIXME: Add $"*{Language}.txt"
            .Union( new[] { Settings.AutoTranslationsFilePath } )
            .Select( x => x.Replace( "/", "\\" ) )
            .Distinct()
            .OrderBy( x => x )
            .ToArray();
      }

      private void RemovedUnusedReferences( object state )
      {
         while( true )
         {
            //// What a brilliant solution...
            //try
            //{
            //   AutoTranslateClient.RemoveUnusedClients();
            //}
            //catch( Exception e )
            //{
            //   Console.WriteLine( "An unexpected error occurred in XUnity.AutoTranslator: " + Environment.NewLine + e );
            //}
            //finally
            //{
            //   Thread.Sleep( 1000 * 20 );
            //}

            //try
            //{
            //   AutoTranslateClient.RemoveUnusedClients();
            //}
            //catch( Exception e )
            //{
            //   Console.WriteLine( "An unexpected error occurred in XUnity.AutoTranslator: " + Environment.NewLine + e );
            //}
            //finally
            //{
            //   Thread.Sleep( 1000 * 20 );
            //}

            try
            {
               //AutoTranslateClient.RemoveUnusedClients();
               ObjectExtensions.Cull();
            }
            catch( Exception e )
            {
               Console.WriteLine( "An unexpected error occurred in XUnity.AutoTranslator: " + Environment.NewLine + e );
            }
            finally
            {
               Thread.Sleep( 1000 * 60 );
            }
         }
      }

      private void SaveTranslationsLoop( object state )
      {
         try
         {
            while( true )
            {
               if( _newTranslations.Count > 0 )
               {
                  lock( _writeToFileSync )
                  {
                     if( _newTranslations.Count > 0 )
                     {
                        using( var stream = File.Open( Settings.AutoTranslationsFilePath, FileMode.Append, FileAccess.Write ) )
                        using( var writer = new StreamWriter( stream, Encoding.UTF8 ) )
                        {
                           foreach( var kvp in _newTranslations )
                           {
                              writer.WriteLine( TextHelper.Encode( kvp.Key ) + '=' + TextHelper.Encode( kvp.Value ) );
                           }
                           writer.Flush();
                        }
                        _newTranslations.Clear();
                     }
                  }
               }
               else
               {
                  Thread.Sleep( 5000 );
               }
            }
         }
         catch( Exception e )
         {
            Console.WriteLine( e );
         }
      }

      /// <summary>
      /// Loads the translations found in Translation.{lang}.txt
      /// </summary>
      private void LoadTranslations()
      {
         try
         {
            lock( _writeToFileSync )
            {
               Directory.CreateDirectory( Path.Combine( Config.Current.DataPath, Settings.TranslationDirectory ) );
               Directory.CreateDirectory( Path.GetDirectoryName( Path.Combine( Config.Current.DataPath, Settings.OutputFile ) ) );

               foreach( var fullFileName in GetTranslationFiles() )
               {
                  if( File.Exists( fullFileName ) )
                  {
                     string[] translations = File.ReadAllLines( fullFileName, Encoding.UTF8 );
                     foreach( string translation in translations )
                     {
                        string[] kvp = translation.Split( new char[] { '=', '\t' }, StringSplitOptions.None );
                        if( kvp.Length == 2 )
                        {
                           string key = TextHelper.Decode( kvp[ 0 ].Trim() );
                           string value = TextHelper.Decode( kvp[ 1 ].Trim() );

                           if( !string.IsNullOrEmpty( key ) && !string.IsNullOrEmpty( value ) )
                           {
                              AddTranslation( key, value );
                           }
                        }
                     }
                  }
               }
            }
         }
         catch( Exception e )
         {
            Console.WriteLine( e );
         }
      }

      private void AddTranslation( string key, string value )
      {
         _translations[ key ] = value;
         _translatedTexts.Add( value );

         if( Settings.IgnoreWhitespaceInKeys )
         {
            var newKey = key.RemoveWhitespace();
            _translations[ newKey ] = value;
         }
      }

      private bool TryGetTranslation( string key, out string value )
      {
         return _translations.TryGetValue( key, out value ) || ( Settings.IgnoreWhitespaceInKeys && _translations.TryGetValue( key.RemoveWhitespace(), out value ) );
      }

      private string Any_TextChanged( object graphic, string text )
      {
         return TranslateOrQueueWebJob( graphic, text, true );
      }

      private void NGUITextEvents_TextChanged( object graphic )
      {
         TranslateOrQueueWebJob( graphic, null, true );
      }

      private void IMGUITextEvents_GUIContentChanged( object content )
      {
         TranslateOrQueueWebJob( content, null, true );
      }

      private void UguiTextEvents_OnTextChanged( object text )
      {
         TranslateOrQueueWebJob( text, null, false );
      }

      private void UguiTextEvents_OnTextAwaken( object text )
      {
         TranslateOrQueueWebJob( text, null, true );
      }

      private void TextMeshProHooks_OnTextChanged( object text )
      {
         TranslateOrQueueWebJob( text, null, false );
      }

      private void TextMeshProHooks_OnTextAwaken( object text )
      {
         TranslateOrQueueWebJob( text, null, true );
      }

      private void SetTranslatedText( object ui, string text, TranslationInfo info )
      {
         info?.SetTranslatedText( text );

         if( _isInTranslatedMode )
         {
            SetText( ui, text, true, info );
         }
      }


      /// <summary>
      /// Sets the text of a UI  text, while ensuring this will not fire a text changed event.
      /// </summary>
      private void SetText( object ui, string text, bool isTranslated, TranslationInfo info )
      {
         if( !info?.IsCurrentlySettingText ?? true )
         {
            try
            {
               UGUIHooks.TextChanged -= UguiTextEvents_OnTextChanged;
               NGUIHooks.TextChanged -= NGUITextEvents_TextChanged;
               TextMeshProHooks.TextChanged -= TextMeshProHooks_OnTextChanged;
               if( info != null )
               {
                  info.IsCurrentlySettingText = true;
               }


               if( ui is Text )
               {
                  ( (Text)ui ).text = text;
               }
               else if( ui is GUIContent )
               {
                  ( (GUIContent)ui ).text = text;
               }
               else
               {
                  // fallback to reflective approach
                  var type = ui.GetType();
                  type.GetProperty( TextPropertyName )?.GetSetMethod()?.Invoke( ui, new[] { text } );
               }

               if( isTranslated )
               {
                  info?.ResizeUI( ui );
               }
               else
               {
                  info?.UnresizeUI( ui );
               }
            }
            finally
            {
               UGUIHooks.TextChanged += UguiTextEvents_OnTextChanged;
               NGUIHooks.TextChanged += NGUITextEvents_TextChanged;
               TextMeshProHooks.TextChanged += TextMeshProHooks_OnTextChanged;
               if( info != null )
               {
                  info.IsCurrentlySettingText = false;
               }
            }
         }
      }

      private string GetText( object ui )
      {
         string text = null;

         if( ui is Text )
         {
            text = ( (Text)ui ).text;
         }
         else if( ui is GUIContent )
         {
            text = ( (GUIContent)ui ).text;
         }
         else
         {
            text = (string)ui.GetType()?.GetProperty( TextPropertyName )?.GetValue( ui, null );
         }

         return text ?? string.Empty;
      }

      /// <summary>
      /// Determines if a text should be translated.
      /// </summary>
      private bool IsTranslatable( string str )
      {
         return TextHelper.ContainsJapaneseSymbols( str ) && str.Length <= Settings.MaxCharactersPerTranslation && !_translatedTexts.Contains( str );
      }

      public bool ShouldTranslate( object ui )
      {
         var cui = ui as Component;
         if( cui != null )
         {
            var go = cui.gameObject;
            var isDummy = go.IsDummy();
            if( isDummy )
            {
               return false;
            }

            var inputField = cui.gameObject.GetFirstComponentInSelfOrAncestor( Constants.Types.InputField )
               ?? cui.gameObject.GetFirstComponentInSelfOrAncestor( Constants.Types.TMP_InputField );

            return inputField == null;
         }

         return true;
      }

      private string TranslateOrQueueWebJob( object ui, string text, bool isAwakening )
      {
         var info = ui.GetTranslationInfo( isAwakening );
         if( !info?.IsAwake ?? false )
         {
            return null;
         }
         if( _ongoingOperations.Contains( ui ) )
         {
            return null;
         }


         if( Settings.Delay == 0 || !SupportsStabilization( ui ) )
         {
            return TranslateOrQueueWebJobImmediate( ui, text, info );
         }
         else
         {
            StartCoroutine(
               DelayForSeconds( Settings.Delay, () =>
               {
                  TranslateOrQueueWebJobImmediate( ui, text, info );
               } ) );
         }

         return null;
      }

      public static bool IsAlreadyTranslating( TranslationInfo info )
      {
         if( info == null ) return false;

         return info.IsCurrentlySettingText;
      }

      /// <summary>
      /// Translates the string of a UI  text or queues it up to be translated
      /// by the HTTP translation service.
      /// </summary>
      private string TranslateOrQueueWebJobImmediate( object ui, string text, TranslationInfo info )
      {
         // Get the trimmed text
         text = ( text ?? GetText( ui ) ).Trim();
         info?.Reset( text );

         // Ensure that we actually want to translate this text and its owning UI element. 
         if( !string.IsNullOrEmpty( text ) && IsTranslatable( text ) && ShouldTranslate( ui ) && !IsAlreadyTranslating( info ) )
         {
            // if we already have translation loaded in our _translatios dictionary, simply load it and set text
            string translation;
            if( TryGetTranslation( text, out translation ) )
            {
               if( !string.IsNullOrEmpty( translation ) )
               {
                  SetTranslatedText( ui, translation, info );
                  return translation;
               }
            }
            else
            {
               if( SupportsStabilization( ui ) )
               {
                  // if we dont know what text to translate it to, we need to figure it out.
                  // this might take a while, so add the UI text component to the ongoing operations
                  // list, so we dont start multiple operations for it, as its text might be constantly
                  // changing.
                  _ongoingOperations.Add( ui );

                  // start a coroutine, that will execute once the string of the UI text has stopped
                  // changing. For all texts except 'story' texts, this will add a delay for exactly 
                  // 0.5s to the translation. This is barely noticable.
                  //
                  // on the other hand, for 'story' texts, this will take the time that it takes
                  // for the text to stop 'scrolling' in.
                  try
                  {
                     StartCoroutine(
                        WaitForTextStablization(
                           ui: ui,
                           delay: 0.5f,
                           maxTries: 100, // 100 tries == 50 seconds
                           currentTries: 0,
                           onMaxTriesExceeded: () =>
                           {
                              _ongoingOperations.Remove( ui );
                           },
                           onTextStabilized: stabilizedText =>
                           {
                              _ongoingOperations.Remove( ui );

                              if( !string.IsNullOrEmpty( stabilizedText ) )
                              {
                                 info?.Reset( stabilizedText );

                                 // once the text has stabilized, attempt to look it up
                                 if( TryGetTranslation( stabilizedText, out translation ) )
                                 {
                                    if( !string.IsNullOrEmpty( translation ) )
                                    {
                                       SetTranslatedText( ui, translation, info );
                                    }
                                 }

                                 if( translation == null )
                                 {
                                    // Lets try not to spam a service that might not be there...
                                    if( AutoTranslateClient.IsConfigured && _consecutiveErrors < Settings.MaxErrors )
                                    {
                                       var job = new TranslationJob { UI = ui, UntranslatedText = stabilizedText };
                                       _unstartedJobs.Add( job );
                                    }
                                    else
                                    {
                                       _newUntranslated.Add( stabilizedText );
                                    }
                                 }
                              }

                           } ) );
                  }
                  catch( Exception )
                  {
                     _ongoingOperations.Remove( ui );
                  }
               }
               else
               {
                  if( !_startedOperationsForNonStabilizableComponents.Contains( text ) )
                  {
                     _startedOperationsForNonStabilizableComponents.Add( text );

                     // Lets try not to spam a service that might not be there...
                     if( AutoTranslateClient.IsConfigured && _consecutiveErrors < Settings.MaxErrors )
                     {
                        var job = new TranslationJob { UntranslatedText = text };
                        _unstartedJobs.Add( job );
                     }
                     else
                     {
                        _newUntranslated.Add( text );
                     }
                  }
               }
            }
         }

         return null;
      }

      public bool SupportsStabilization( object ui )
      {
         return !( ui is GUIContent );
      }

      /// <summary>
      /// Utility method that allows me to wait to call an action, until
      /// the text has stopped changing. This is important for 'story'
      /// mode text, which 'scrolls' into place slowly.
      /// </summary>
      public IEnumerator WaitForTextStablization( object ui, float delay, int maxTries, int currentTries, Action<string> onTextStabilized, Action onMaxTriesExceeded )
      {
         if( currentTries < maxTries ) // shortcircuit
         {
            var beforeText = GetText( ui );
            yield return new WaitForSeconds( delay );
            var afterText = GetText( ui );

            if( beforeText == afterText )
            {
               onTextStabilized( afterText.Trim() );
            }
            else
            {
               StartCoroutine( WaitForTextStablization( ui, delay, maxTries, currentTries + 1, onTextStabilized, onMaxTriesExceeded ) );
            }
         }
         else
         {
            onMaxTriesExceeded();
         }
      }

      public IEnumerator DelayForSeconds( float delay, Action onContinue )
      {
         yield return new WaitForSeconds( delay );

         onContinue();
      }

      public void Update()
      {
         try
         {
            KickoffTranslations();
            FinishTranslations();

            if( Input.anyKey )
            {
               if( Settings.EnablePrintHierarchy && ( Input.GetKey( KeyCode.LeftAlt ) || Input.GetKey( KeyCode.RightAlt ) ) && Input.GetKeyDown( KeyCode.Y ) )
               {
                  PrintObjects();
               }
               else if( ( Input.GetKey( KeyCode.LeftAlt ) || Input.GetKey( KeyCode.RightAlt ) ) && Input.GetKeyDown( KeyCode.T ) )
               {
                  ToggleTranslation();
               }
               else if( ( Input.GetKey( KeyCode.LeftAlt ) || Input.GetKey( KeyCode.RightAlt ) ) && Input.GetKeyDown( KeyCode.D ) )
               {
                  DumpUntranslated();
               }
               else if( ( Input.GetKey( KeyCode.LeftAlt ) || Input.GetKey( KeyCode.RightAlt ) ) && Input.GetKeyDown( KeyCode.R ) )
               {
                  ReloadTranslations();
               }
            }
         }
         catch( Exception e )
         {
            Console.WriteLine( e );
         }
      }

      private void KickoffTranslations()
      {
         while( AutoTranslateClient.HasAvailableClients && _unstartedJobs.Count > 0 )
         {
            var job = _unstartedJobs[ _unstartedJobs.Count - 1 ];
            _unstartedJobs.RemoveAt( _unstartedJobs.Count - 1 );

            // lets see if the text should still be translated before kicking anything off
            if( job.UI != null )
            {
               var text = GetText( job.UI ).Trim();
               if( text != job.UntranslatedText )
               {
                  continue; // just ignore this UI component, as the text has already changed anyway (maybe from game, maybe from other plugin)
               }
            }

            //StartCoroutine( AutoTranslateClient.TranslateByWWW( job.UntranslatedText.ChangeToSingleLineForDialogue(), Settings.FromLanguage, Settings.Language, translatedText =>
            //{
            //   _consecutiveErrors = 0;

            //   job.TranslatedText = translatedText;

            //   if( !string.IsNullOrEmpty( translatedText ) )
            //   {
            //      lock( _writeToFileSync )
            //      {
            //         _newTranslations[ job.UntranslatedText ] = translatedText;
            //      }
            //   }

            //   _completedJobs.Add( job );
            //},
            //() =>
            //{
            //   _consecutiveErrors++;
            //} ) );

            StartCoroutine( AutoTranslateClient.TranslateByWWW( job.UntranslatedText.ChangeToSingleLineForDialogue(), Settings.FromLanguage, Settings.Language, translatedText =>
            {
               _consecutiveErrors = 0;

               job.TranslatedText = translatedText;

               if( !string.IsNullOrEmpty( translatedText ) )
               {
                  lock( _writeToFileSync )
                  {
                     _newTranslations[ job.UntranslatedText ] = translatedText;
                  }
               }

               _completedJobs.Add( job );
            },
            () =>
            {
               _consecutiveErrors++;
            } ) );
         }
      }

      private void FinishTranslations()
      {
         if( _completedJobs.Count > 0 )
         {
            for( int i = _completedJobs.Count - 1 ; i >= 0 ; i-- )
            {
               var job = _completedJobs[ i ];
               _completedJobs.RemoveAt( i );

               if( !string.IsNullOrEmpty( job.TranslatedText ) )
               {
                  if( job.UI != null )
                  {
                     // update the original text, but only if it has not been chaanged already for some reason (could be other translator plugin or game itself)
                     var text = GetText( job.UI ).Trim();
                     if( text == job.UntranslatedText )
                     {
                        var info = job.UI.GetTranslationInfo( false );
                        SetTranslatedText( job.UI, job.TranslatedText, info );
                     }
                  }

                  AddTranslation( job.UntranslatedText, job.TranslatedText );
               }
            }
         }
      }

      private void ReloadTranslations()
      {
         LoadTranslations();

         foreach( var kvp in ObjectExtensions.GetAllRegisteredObjects() )
         {
            var info = kvp.Value as TranslationInfo;
            if( info != null && !string.IsNullOrEmpty( info.OriginalText ) )
            {
               if( TryGetTranslation( info.OriginalText, out string translatedText ) && !string.IsNullOrEmpty( translatedText ) )
               {
                  SetTranslatedText( kvp.Key, translatedText, info );
               }
            }
         }
      }

      private string CalculateDumpFileName()
      {
         int idx = 0;
         string fileName = null;
         do
         {
            idx++;
            fileName = $"UntranslatedDump{idx}.txt";
         }
         while( File.Exists( fileName ) );

         return fileName;
      }

      private void DumpUntranslated()
      {
         if( _newUntranslated.Count > 0 )
         {
            using( var stream = File.Open( CalculateDumpFileName(), FileMode.Append, FileAccess.Write ) )
            using( var writer = new StreamWriter( stream, Encoding.UTF8 ) )
            {
               foreach( var untranslated in _newUntranslated )
               {
                  writer.WriteLine( TextHelper.Encode( untranslated ) + '=' );
               }
               writer.Flush();
            }

            _newUntranslated.Clear();
         }
      }

      private void ToggleTranslation()
      {
         _isInTranslatedMode = !_isInTranslatedMode;

         if( _isInTranslatedMode )
         {
            // make sure we use the translated version of all texts
            foreach( var kvp in ObjectExtensions.GetAllRegisteredObjects() )
            {
               var ui = kvp.Key;
               var info = (TranslationInfo)kvp.Value;

               if( info != null && info.IsTranslated )
               {
                  SetText( ui, info.TranslatedText, true, info );
               }
            }
         }
         else
         {
            // make sure we use the original version of all texts
            foreach( var kvp in ObjectExtensions.GetAllRegisteredObjects() )
            {
               var ui = kvp.Key;
               var info = (TranslationInfo)kvp.Value;

               if( info != null )
               {
                  SetText( ui, info.OriginalText, false, info );
               }
            }
         }
      }

      private void PrintObjects()
      {

         using( var stream = File.Open( Path.Combine( Environment.CurrentDirectory, "hierarchy.txt" ), FileMode.Create ) )
         using( var writer = new StreamWriter( stream ) )
         {
            foreach( var root in GetAllRoots() )
            {
               TraverseChildren( writer, root, "" );
            }

            writer.Flush();
         }
      }

      private IEnumerable<GameObject> GetAllRoots()
      {
         var objects = GameObject.FindObjectsOfType<GameObject>();
         foreach( var obj in objects )
         {
            if( obj.transform.parent == null )
            {
               yield return obj;
            }
         }
      }

      private void TraverseChildren( StreamWriter writer, GameObject obj, string identation )
      {
         var layer = LayerMask.LayerToName( obj.gameObject.layer );
         var components = string.Join( ", ", obj.GetComponents<Component>().Select( x => x.GetType().Name ).ToArray() );
         var line = string.Format( "{0,-50} {1,100}",
            identation + obj.gameObject.name + " [" + layer + "]",
            components );

         writer.WriteLine( line );

         for( int i = 0 ; i < obj.transform.childCount ; i++ )
         {
            var child = obj.transform.GetChild( i );
            TraverseChildren( writer, child.gameObject, identation + " " );
         }
      }
   }
}
