/// Copyright (c) 2012 Ecma International.  All rights reserved. 
/**
 * @path ch15/15.2/15.2.3/15.2.3.3/15.2.3.3-4-192.js
 * @description Object.getOwnPropertyDescriptor returns data desc (all false) for properties on built-ins (String (instance).length)
 */


function testcase() {
  var s = new String("abc");
  var desc = Object.getOwnPropertyDescriptor(s, "length");

  if (desc.writable === false &&
      desc.enumerable === false &&
      desc.configurable === false &&
      desc.hasOwnProperty('get') === false &&
      desc.hasOwnProperty('set') === false) {
    return true;
  }
 }
runTestCase(testcase);
