## SIP Speech Prototype

The prototype demonstrates how a SIP (or VoIP) call can be integrated with Microsoft's Azure Speech services.

### Set up

The Azure secret key and region need to be set in the `appsettings.json` file.

### Usage

From a Windows command console change to the `SipSpeech`, the directory containing the `SipSpeech.csproj` file and type:

`dotnet run`

When executed the program will attempt to listen for SIP requests on UDP port 5060. If that port is already in use edit the constant at the top of `Program.cs` to specify a different port.

Upon successfully starting the output should appear as below:

````
c:\Dev\sipsorcery\sipspeech\SipSpeech>dotnet run
SIP Speech Server example.
Press 'd' to send a random DTMF tone to the newest call.
Press 'h' to hangup the oldest call.
Press 'H' to hangup all calls.
Press 'l' to list current calls.
Press 'r' to list current registrations.
Press 'q' to quit.
[10:16:08 INF] SIP UDP Channel created for 0.0.0.0:5060.
````

### Calling

From a SIP device or softphone pace a call to any of the system hosting the application's IP addresses. How to do this will depend on the SIP device or softphone.

A recommended softphone is [MicroSIP](https://www.microsip.org/). To configure it to be able to call the application the steps are:

 - Click the downwards arrow icon on the right hand side of the menu bar,
 - If there is already a `localhost` account enabled no further action should be required. Enter any digit and press `Call`,
 - If there is no `localhost` account select `Add Account` and complete with:
   - `Account Name` (descriptive only): `sipspeech` or whatever you like,
   - `Username` (not used but required): 'sipspeech`,
   - `Domain`: `127.0.0.1` or the IP address of the machine running the `sipspeech` prototype,
   - Click `Save`
   
One additional step is to ensure that the softphone has the `G722` codec enabled (it's the only codec supported by the `sipspeech` program):
  - Click the downwards arrow icon on the right hand side of the menu bar,
  - Select `Settings`,
  - In the `Enabled Codecs` box ensure that `G.722 16 KHz` is present.
  
### Operation

When the program receives a call it will automatically answer.

To test the text-to-speech integration press a number on the softphone keypad to send a DTMF tone. Some hard coded text messages have been wired up to keys `0`, `1` and `2`. A generic prompt is played for all other keys.

To test the speech-to-text integration speak into the microphone and within a short space of time an event from the `Speech Recognizer` service should appear on the console. An example is shown below:

````
[10:34:47 DBG] Speech recognizer recognizing result=ResultId:5cf48a9763f6465ba4cd29de699c43c6 Reason:RecognizingSpeech Recognized text:<hello>. Json:{"Duration":3100000,"Id":"a3372807460346c6b3900a6c6e9d4d80","Offset":12500000,"Text":"hello"} bbe2c63d3ba74ddf8d368fe49c3b8a9e.
[10:34:48 DBG] Speech recognizer recognizing result=ResultId:a9e20cf6443c4fa2849996d139e5d949 Reason:RecognizingSpeech Recognized text:<hello world>. Json:{"Duration":8500000,"Id":"7fee9d4ca5a04976b4199b653f49fd72","Offset":12500000,"Text":"hello world"} bbe2c63d3ba74ddf8d368fe49c3b8a9e.
[10:34:48 DBG] Speech recognizer recognized result=Hello World. bbe2c63d3ba74ddf8d368fe49c3b8a9e.
````
 
 
