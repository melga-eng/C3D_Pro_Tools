// Creates the C3D Pro Tools feedback Google Form, with its answers collected in a Google Sheet.
// Run once at https://script.google.com (New project, paste this, Run, allow access). The links are printed in the log.
// Every Run makes a NEW form: open the "Edit the form" link from the latest run, and delete older copies from Drive.
// Screenshots: Apps Script cannot add a File upload question. Add it by hand under question 4: File upload, title
// "Screenshots (optional)", images only, up to 5 files, 10 MB, not required. Note: an upload question makes everyone
// sign in to Google to send the form.
function createFeedbackForm() {
  var form = FormApp.create('C3D Pro Tools - Feedback');
  form.setDescription('Two minutes. Only the first question is required. ' +
                      'Every answer is read, and it decides what gets fixed and built next.');
  form.setCollectEmail(false);
  form.setAllowResponseEdits(false);
  form.setLimitOneResponsePerUser(false);
  form.setShowLinkToRespondAgain(true);
  form.setConfirmationMessage('Thank you! Your feedback goes straight into the next version of C3D Pro Tools.');
  Logger.log('Edit the form: ' + form.getEditUrl());

  // 1. Which tool (the only required question).
  form.addCheckboxItem().setTitle('Which tool are you giving feedback on?').setRequired(true)
      .setChoiceValues(['Navisworks Import', 'Surface from Solids', 'Rebuild Network', 'Installing C3D Pro Tools']);
  Logger.log('Added question 1');

  // 2. Did it work: install problems are kept apart from tool problems.
  form.addMultipleChoiceItem().setTitle('Did it work?')
      .setChoiceValues(['Yes, as I expected', 'Partly', 'No', 'I could not install it']);
  Logger.log('Added question 2');

  // 3. A number that can be compared across tools and versions.
  form.addScaleItem().setTitle('How useful is it for your work?')
      .setBounds(1, 5).setLabels('Not useful', 'Saves me a lot of time');
  Logger.log('Added question 3');

  // 4. The problem, in the steps needed to reproduce it.
  form.addParagraphTextItem().setTitle('If something went wrong: what did you do, what did you expect, and what happened?')
      .setHelpText('Exact steps help most. For an error, paste the last lines of the log file (C3D Pro Tools > About shows where it is).');
  Logger.log('Added question 4');

  // 5. What to build next.
  form.addParagraphTextItem().setTitle('What would make it more useful, or which tool would you like next?');
  Logger.log('Added question 5');

  // 6. Who is asking, to know whose needs come first.
  var work = form.addMultipleChoiceItem();
  work.setTitle('Your main work')
      .setChoiceValues(['Utilities / dry utilities', 'Drainage / wet utilities', 'Roads and highways', 'Airports', 'BIM / coordination']);
  work.showOtherOption(true);
  Logger.log('Added question 6');

  // 7. One contact field instead of two.
  var contact = form.addTextItem().setTitle('Your email, if you would like a reply (optional)');
  contact.setValidation(FormApp.createTextValidation().requireTextIsEmail().build());
  Logger.log('Added question 7');

  var sheet = SpreadsheetApp.create('C3D Pro Tools - Feedback (answers)');
  form.setDestination(FormApp.DestinationType.SPREADSHEET, sheet.getId());

  Logger.log('Questions in the form: ' + form.getItems().length + ' (should be 7)');
  Logger.log('Form link to share: ' + form.getPublishedUrl());
  Logger.log('Answers sheet: ' + sheet.getUrl());
}
