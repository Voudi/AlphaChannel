#!/bin/bash
GIST="77c661ec1597925c2856d649a0cfc2d8"

git clone https://gist.github.com/Voudi/$GIST.git

cp AlphaChannel.zip $GIST/
cp pluginmaster.json $GIST/pluginmaster.json
cd $GIST
git show-ref 
git add AlphaChannel.zip
git add pluginmaster.json

git commit -m "Adding Zip to Gist"
 
git push origin main
#If prompted, provide your Github credentials associated with the gist
cd ..
rm -rf $GIST
rm -rf AlphaChannel.zip
rm -rf pluginmaster.json